using CoreFoundry.Domain.Schema;

namespace CoreFoundry.Application.Templates;

/// <summary>
/// A small shop: customers and their addresses, a category tree, products, orders with items and
/// payments, and reviews. Every column type appears, and all three on-delete rules. The sample data is
/// made up (names, <c>example.com</c> emails); order totals match their items.
/// </summary>
internal static class EcommerceTemplate
{
    public static SchemaTemplate Create() => new(
        "ecommerce",
        "E-commerce",
        "Customers, addresses, a category tree, products, orders with items and payments, and reviews.",
        [
            new("customers",
            [
                Varchar("email", 254, unique: true),
                Varchar("full_name", 120),
                Varchar("phone", 30, nullable: true),
                new("birth_date", DataType.Date, Nullable: true),
                CreatedAt(),
            ],
            Read: AccessLevel.Admin, Write: AccessLevel.Admin), // personal data
            new("addresses",
            [
                Reference("customer_id", "customers", ReferenceAction.Cascade),
                Varchar("line1", 200),
                Varchar("line2", 200, nullable: true),
                Varchar("city", 100),
                Varchar("postal_code", 20, nullable: true),
                Varchar("country_code", 2),
                new("is_default", DataType.Bool, Default: "false"),
            ],
            Read: AccessLevel.Admin, Write: AccessLevel.Admin), // personal data
            new("categories",
            [
                Varchar("name", 100, unique: true),
                Varchar("slug", 120, unique: true),
                Reference("parent_id", "categories", ReferenceAction.SetNull, nullable: true),
            ],
            Read: AccessLevel.Public, Write: AccessLevel.Admin), // the catalog is public, only staff edit it
            new("products",
            [
                Reference("category_id", "categories", ReferenceAction.SetNull, nullable: true),
                Varchar("sku", 40, unique: true),
                Varchar("name", 200),
                new("description", DataType.Text, Nullable: true),
                Money("price", 10),
                new("stock", DataType.Int, Default: "0"),
                new("is_active", DataType.Bool, Default: "true"),
                new("attributes", DataType.Json, Nullable: true),
                CreatedAt(),
            ],
            Read: AccessLevel.Public, Write: AccessLevel.Admin), // the catalog is public, only staff edit it
            new("orders",
            [
                new("public_id", DataType.Uuid, Unique: true, Default: "UUID()"),
                Reference("customer_id", "customers", ReferenceAction.Restrict),
                Reference("shipping_address_id", "addresses", ReferenceAction.SetNull, nullable: true),
                Varchar("status", 20, @default: "pending"),
                Money("total", 12, @default: "0.00"),
                new("placed_at", DataType.DateTime, Default: "CURRENT_TIMESTAMP"),
            ],
            // Only staff; customers placing their own orders needs the Owner level (phase-8 §6 q.1) — a
            // Signed-in write here (with Admin read) would also let any customer PUT/DELETE any order by id,
            // which "write not wider than read" forbids.
            Read: AccessLevel.Admin, Write: AccessLevel.Admin),
            new("order_items",
            [
                Reference("order_id", "orders", ReferenceAction.Cascade),
                Reference("product_id", "products", ReferenceAction.Restrict),
                new("quantity", DataType.Int, Default: "1"),
                Money("unit_price", 10),
            ],
            Read: AccessLevel.Admin, Write: AccessLevel.Admin), // same as orders
            new("payments",
            [
                Reference("order_id", "orders", ReferenceAction.Cascade),
                Money("amount", 12),
                Varchar("method", 30),
                Varchar("status", 20, @default: "authorized"),
                new("paid_at", DataType.DateTime, Nullable: true),
            ],
            Read: AccessLevel.Admin, Write: AccessLevel.Admin), // money
            new("reviews",
            [
                Reference("product_id", "products", ReferenceAction.Cascade),
                Reference("customer_id", "customers", ReferenceAction.Cascade),
                new("rating", DataType.Int),
                new("comment", DataType.Text, Nullable: true),
                CreatedAt(),
            ],
            Read: AccessLevel.Public, Write: AccessLevel.SignedIn), // anyone reads, customers write
        ],
        [.. Customers(), .. Addresses(), .. Categories(), .. Products(), .. Orders(), .. Reviews()]);

    private static TemplateColumn Varchar(string name, int length, bool nullable = false, bool unique = false, string? @default = null) =>
        new(name, DataType.Varchar, Length: length, Nullable: nullable, Unique: unique, Default: @default);

    private static TemplateColumn Money(string name, int precision, string? @default = null) =>
        new(name, DataType.Decimal, Precision: precision, Scale: 2, Default: @default);

    private static TemplateColumn Reference(string name, string table, ReferenceAction onDelete, bool nullable = false) =>
        new(name, DataType.BigInt, Nullable: nullable, References: table, OnDelete: onDelete);

    private static TemplateColumn CreatedAt() => new("created_at", DataType.DateTime, Default: "CURRENT_TIMESTAMP");

    private static SampleRow Row(string table, string key, params (string Column, object? Value)[] values) =>
        new(table, key, values.ToDictionary(value => value.Column, value => value.Value));

    private static IEnumerable<SampleRow> Customers() =>
    [
        Row("customers", "ann", ("email", "ann.lee@example.com"), ("full_name", "Ann Lee"), ("phone", "+1 555 0100"), ("birth_date", "1988-04-12")),
        Row("customers", "bob", ("email", "bob.martin@example.com"), ("full_name", "Bob Martin"), ("phone", null), ("birth_date", "1975-11-03")),
        Row("customers", "chen", ("email", "chen.wei@example.com"), ("full_name", "Chen Wei"), ("phone", "+44 20 7946 0000"), ("birth_date", null)),
        Row("customers", "dara", ("email", "dara.okafor@example.com"), ("full_name", "Dara Okafor"), ("phone", "+234 1 555 0199"), ("birth_date", "1995-07-21")),
        Row("customers", "eli", ("email", "eli.novak@example.com"), ("full_name", "Eli Novak"), ("phone", null), ("birth_date", "2000-02-29")),
        Row("customers", "fatima", ("email", "fatima.hassan@example.com"), ("full_name", "Fatima Hassan"), ("phone", "+20 2 5550 0123"), ("birth_date", "1991-09-30")),
    ];

    private static IEnumerable<SampleRow> Addresses() =>
    [
        Row("addresses", "ann_home", ("customer_id", "@ann"), ("line1", "12 Harbor Street"), ("city", "Portland"), ("postal_code", "97201"), ("country_code", "US"), ("is_default", true)),
        Row("addresses", "ann_work", ("customer_id", "@ann"), ("line1", "400 Market Avenue"), ("line2", "Floor 5"), ("city", "Portland"), ("postal_code", "97204"), ("country_code", "US")),
        Row("addresses", "bob_home", ("customer_id", "@bob"), ("line1", "8 Elm Road"), ("city", "Denver"), ("postal_code", "80202"), ("country_code", "US"), ("is_default", true)),
        Row("addresses", "chen_home", ("customer_id", "@chen"), ("line1", "27 Kings Lane"), ("city", "London"), ("postal_code", "SW1A 1AA"), ("country_code", "GB"), ("is_default", true)),
        Row("addresses", "dara_home", ("customer_id", "@dara"), ("line1", "3 Admiralty Way"), ("city", "Lagos"), ("country_code", "NG"), ("is_default", true)),
        Row("addresses", "eli_home", ("customer_id", "@eli"), ("line1", "Vinohradska 10"), ("city", "Prague"), ("postal_code", "120 00"), ("country_code", "CZ"), ("is_default", true)),
        Row("addresses", "fatima_home", ("customer_id", "@fatima"), ("line1", "15 Nile Corniche"), ("line2", "Apt 7"), ("city", "Cairo"), ("postal_code", "11511"), ("country_code", "EG"), ("is_default", true)),
    ];

    private static IEnumerable<SampleRow> Categories() =>
    [
        Row("categories", "electronics", ("name", "Electronics"), ("slug", "electronics")),
        Row("categories", "books", ("name", "Books"), ("slug", "books")),
        Row("categories", "home", ("name", "Home & Kitchen"), ("slug", "home-kitchen")),
        Row("categories", "phones", ("name", "Phones"), ("slug", "phones"), ("parent_id", "@electronics")),
        Row("categories", "laptops", ("name", "Laptops"), ("slug", "laptops"), ("parent_id", "@electronics")),
    ];

    private static IEnumerable<SampleRow> Products() =>
    [
        Product("aurora_phone", "phones", "PH-AUR-X", "Aurora Phone X", "699.00", 25, new { color = "graphite", storage_gb = 128 }, "A 6.1-inch phone with a two-day battery."),
        Product("nimbus_phone", "phones", "PH-NIM-L", "Nimbus Phone Lite", "349.00", 40, new { color = "sky blue", storage_gb = 64 }),
        Product("atlas_laptop", "laptops", "LT-ATL-14", "Atlas 14 Laptop", "1199.00", 10, new { ram_gb = 16, storage_gb = 512 }, "14-inch, 16 GB RAM, 512 GB SSD."),
        Product("orbit_laptop", "laptops", "LT-ORB-13", "Orbit 13 Ultrabook", "949.00", 8, new { ram_gb = 8, storage_gb = 256 }),
        Product("earbuds", "electronics", "AU-WAVE-01", "Wave Wireless Earbuds", "89.90", 120, new { color = "white", noise_cancelling = true }),
        Product("charger", "electronics", "PW-65W-C", "65W USB-C Charger", "29.99", 200, null),
        Product("book_data", "books", "BK-DDS-01", "Designing Data Systems", "39.50", 60, new { pages = 520, format = "paperback" }),
        Product("book_sql", "books", "BK-SQL-01", "SQL from Scratch", "24.99", 80, new { pages = 312, format = "paperback" }),
        Product("book_cooking", "books", "BK-CKG-01", "Weeknight Cooking", "19.00", 35, new { pages = 208, format = "hardcover" }),
        Product("lamp", "home", "HM-LAMP-OAK", "Oak Desk Lamp", "54.00", 30, new { material = "oak" }),
        Product("mug_set", "home", "HM-MUG-4", "Stoneware Mug Set (4)", "32.00", 50, null),
        Product("blanket", "home", "HM-THROW-W", "Wool Throw Blanket", "79.00", 0, new { material = "wool" }, active: false),
    ];

    private static SampleRow Product(
        string key, string category, string sku, string name, string price, int stock, object? attributes, string? description = null, bool active = true) =>
        Row("products", key, ("category_id", "@" + category), ("sku", sku), ("name", name), ("description", description), ("price", price),
            ("stock", stock), ("is_active", active), ("attributes", attributes));

    private static IEnumerable<SampleRow> Orders()
    {
        // (order, customer, address, status, placed at, items (product, quantity, unit price), payment (method, status, paid at) or null)
        var orders = new (string Key, string Customer, string Address, string Status, string PlacedAt, (string Product, int Quantity, decimal Price)[] Items, (string Method, string Status, string? PaidAt)? Payment)[]
        {
            ("o1001", "ann", "ann_home", "delivered", "2026-01-08T10:15:00", [("aurora_phone", 1, 699.00m), ("charger", 1, 29.99m)], ("card", "captured", "2026-01-08T10:16:02")),
            ("o1002", "bob", "bob_home", "delivered", "2026-01-19T18:40:00", [("book_data", 1, 39.50m), ("book_sql", 2, 24.99m)], ("paypal", "captured", "2026-01-19T18:41:30")),
            ("o1003", "chen", "chen_home", "shipped", "2026-02-02T09:05:00", [("atlas_laptop", 1, 1199.00m)], ("card", "captured", "2026-02-02T09:05:45")),
            ("o1004", "dara", "dara_home", "pending", "2026-02-14T21:30:00", [("earbuds", 2, 89.90m)], ("card", "authorized", null)),
            ("o1005", "ann", "ann_work", "shipped", "2026-02-20T12:00:00", [("lamp", 1, 54.00m), ("mug_set", 1, 32.00m)], ("card", "captured", "2026-02-20T12:01:10")),
            ("o1006", "eli", "eli_home", "cancelled", "2026-03-01T08:20:00", [("orbit_laptop", 1, 949.00m)], ("card", "refunded", "2026-03-01T08:21:00")),
            ("o1007", "fatima", "fatima_home", "delivered", "2026-03-11T16:45:00", [("book_cooking", 1, 19.00m), ("mug_set", 1, 32.00m)], ("bank_transfer", "captured", "2026-03-12T09:00:00")),
            ("o1008", "bob", "bob_home", "pending", "2026-03-15T19:10:00", [("nimbus_phone", 1, 349.00m), ("charger", 1, 29.99m)], null),
        };

        foreach (var order in orders)
        {
            var total = order.Items.Sum(item => item.Quantity * item.Price);
            yield return Row("orders", order.Key, ("customer_id", "@" + order.Customer), ("shipping_address_id", "@" + order.Address),
                ("status", order.Status), ("total", Money(total)), ("placed_at", order.PlacedAt));

            for (var i = 0; i < order.Items.Length; i++)
            {
                var item = order.Items[i];
                yield return Row("order_items", $"{order.Key}_{i + 1}", ("order_id", "@" + order.Key), ("product_id", "@" + item.Product),
                    ("quantity", item.Quantity), ("unit_price", Money(item.Price)));
            }

            if (order.Payment is { } payment)
            {
                yield return Row("payments", $"{order.Key}_payment", ("order_id", "@" + order.Key), ("amount", Money(total)),
                    ("method", payment.Method), ("status", payment.Status), ("paid_at", payment.PaidAt));
            }
        }
    }

    private static IEnumerable<SampleRow> Reviews() =>
    [
        Review(1, "aurora_phone", "ann", 5, "Great battery life and a bright screen."),
        Review(2, "charger", "ann", 4, "Charges fast, gets a little warm."),
        Review(3, "book_data", "bob", 5, "The clearest explanation of replication I've read."),
        Review(4, "book_sql", "bob", 4, null),
        Review(5, "atlas_laptop", "chen", 5, "Light, quiet and fast."),
        Review(6, "earbuds", "dara", 3, "Good sound, the case feels cheap."),
        Review(7, "lamp", "ann", 4, "Warm light, solid wood."),
        Review(8, "book_cooking", "fatima", 5, "Every recipe worked the first time."),
        Review(9, "mug_set", "fatima", 4, null),
        Review(10, "orbit_laptop", "eli", 2, "Returned it: the keyboard flexes."),
    ];

    private static SampleRow Review(int number, string product, string customer, int rating, string? comment) =>
        Row("reviews", $"review_{number}", ("product_id", "@" + product), ("customer_id", "@" + customer), ("rating", rating), ("comment", comment));

    /// <summary>Decimals travel as text with two places, like the Data API returns them.</summary>
    private static string Money(decimal value) => value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}
