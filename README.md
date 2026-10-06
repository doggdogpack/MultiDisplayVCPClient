# Multi-Display VCP Client for Macro Deck 3

Control your monitors' hardware VCP features (Brightness, Contrast, Input Source, Audio Volume, Power Mode, and more) across your network using [Macro Deck 3](https://macro-deck.app/).

## Overview

This plugin connects Macro Deck 3 to one or more [MultiDisplayVCP Server](https://github.com/doggdogpack/MultiDisplayVCPServer) instances running on your desktop PCs (Windows, Linux, macOS).

## Features

- **Multi-Server & Multi-Monitor Support**: Manage multiple displays across multiple host machines seamlessly.
- **Dynamic Variable Catalog**: Automatically registers reactive Macro Deck integration variables for every monitor and supported VCP feature (`vars.multidisplay_<connection>_<monitor>_<feature>`).
- **High-Performance Communication**: Uses MagicOnion gRPC over HTTP/2 with automatic fallback to legacy TCP.
- **HMAC-SHA256 Authentication**: Secure sliding-window challenge authentication.
- **Cross-Platform**: Universal plugin supporting Windows (x64), Linux (x64), and macOS (Apple Silicon & Intel).
- **Mobile Control Surface**: Stream live monitor states and trigger actions directly from iOS and Android devices running the Macro Deck mobile client.

## Installation

1. Open Macro Deck 3.
2. Go to the **Extension Store** or install from file:
   - File: `com.multidisplayvcp.client-3.0.0.macroDeckPlugin`
3. Configure your server connection(s) via the plugin setup wizard (Host IP, Port, and Password).

## Building from Source

Requires [.NET 10.0 SDK](https://dotnet.microsoft.com/) and `macrodeck-plugin` CLI:

```bash
dotnet publish -c Release
```

Or build the universal package:
```bash
macrodeck-plugin build
```

## License

MIT License. See [LICENSE.txt](LICENSE.txt) for details.
