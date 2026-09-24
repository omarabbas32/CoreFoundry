import type { NextConfig } from "next";

// Where the ASP.NET Core API listens. Server-side only: the browser never sees it.
const apiOrigin = process.env.API_ORIGIN ?? "http://localhost:5172";

const nextConfig: NextConfig = {
  // The browser only talks to this origin; /api/* is proxied to the API. Same origin means the
  // SameSite=Strict refresh cookie and CORS need no special handling (matches the M5 single-origin setup).
  async rewrites() {
    return [{ source: "/api/:path*", destination: `${apiOrigin}/api/:path*` }];
  },
};

export default nextConfig;
