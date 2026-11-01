import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  cacheComponents: true,
  partialPrefetching: true,
  turbopack: {
    rules: {
      "*.css": {
        loaders: ["@tailwindcss/turbopack"],
        as: "*.css",
      },
    },
  },
  async headers() {
    return [
      {
        source: "/:path*",
        headers: [
          {
            key: "Permissions-Policy",
            value: [
              "accelerometer=(self)",
              "gyroscope=(self)",
              "magnetometer=(self)",
              "geolocation=(self)",
              "ambient-light-sensor=(self)",
              "camera=(self)",
              "microphone=(self)",
            ].join(", "),
          },
          {
            key: "Feature-Policy",
            value:
              "accelerometer 'self'; gyroscope 'self'; magnetometer 'self'; geolocation 'self'",
          },
        ],
      },
    ];
  },
};

export default nextConfig;
