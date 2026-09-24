using CoreFoundry.Domain.Common;
using CoreFoundry.Domain.Schema;
using Shouldly;

namespace CoreFoundry.UnitTests.Domain;

public class IdentifierRulesTests
{
    [Theory]
    [InlineData("books")]
    [InlineData("a")]
    [InlineData("author_id")]
    [InlineData("isbn13")]
    [InlineData("a__b_")]
    [InlineData("identity")]
    [InlineData("xcf_notes")]
    public void Valid_names_are_accepted(string name) =>
        IdentifierRules.Normalize(name, "Table name").ShouldBe(name);

    [Theory]
    [InlineData("1abc")]
    [InlineData("_abc")]
    [InlineData("a-b")]
    [InlineData("a`b")]
    [InlineData("a;drop")]
    [InlineData("a b")]
    [InlineData("a.b")]
    [InlineData("a'b")]
    [InlineData("naïve")]
    public void Names_with_unsafe_characters_are_rejected(string name) =>
        Should.Throw<DomainException>(() => IdentifierRules.Normalize(name, "Table name"))
            .Message.ShouldContain("only letters, digits and underscores");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_names_are_rejected(string? name) =>
        Should.Throw<DomainException>(() => IdentifierRules.Normalize(name, "Column name"))
            .Message.ShouldBe("Column name is required.");

    [Fact]
    public void A_64_character_name_is_the_longest_allowed()
    {
        var name = "a" + new string('b', 63);

        IdentifierRules.Normalize(name, "Table name").ShouldBe(name);
    }

    [Fact]
    public void A_65_character_name_is_rejected() =>
        Should.Throw<DomainException>(() => IdentifierRules.Normalize("a" + new string('b', 64), "Table name"))
            .Message.ShouldBe("Table name must be at most 64 characters.");

    [Theory]
    [InlineData("select")]
    [InlineData("order")]
    [InlineData("table")]
    [InlineData("key")]
    [InlineData("Group")]
    public void MySql_reserved_words_are_rejected(string name) =>
        Should.Throw<DomainException>(() => IdentifierRules.Normalize(name, "Table name"))
            .Message.ShouldContain("reserved word in MySQL");

    [Theory]
    [InlineData("id")]
    [InlineData("ID")]
    public void Id_is_reserved_for_the_primary_key(string name) =>
        Should.Throw<DomainException>(() => IdentifierRules.Normalize(name, "Column name"))
            .Message.ShouldContain("primary key");

    [Theory]
    [InlineData("cf_x")]
    [InlineData("CF_audit")]
    [InlineData("cf_")]
    public void Cf_prefix_is_reserved_for_CoreFoundry(string name) =>
        Should.Throw<DomainException>(() => IdentifierRules.Normalize(name, "Table name"))
            .Message.ShouldContain("reserved by CoreFoundry");

    [Theory]
    [InlineData("Books", "books")]
    [InlineData("  AuthorId ", "authorid")]
    [InlineData("PUBLISHED_AT", "published_at")]
    public void Names_are_trimmed_and_lower_cased(string input, string expected) =>
        IdentifierRules.Normalize(input, "Table name").ShouldBe(expected);

    [Fact]
    public void Reserved_word_list_is_loaded() =>
        IdentifierRules.IsReservedWord("select").ShouldBeTrue();
}
