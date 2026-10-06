# MultiDisplayVCP Client for Macro Deck 3

Control your monitors' hardware DDC/CI settings (Brightness, Contrast, Input Source, Audio Volume, Power Mode, and more) across your local network directly from [Macro Deck 3](https://macro-deck.app/).

> [!IMPORTANT]
> **Not a Standalone Application**  
> This is an extension plugin for **[Macro Deck 3](https://macro-deck.app/)**. It works end-to-end with the **[MultiDisplayVCP Server](https://github.com/doggdogpack/MultiDisplayVCPServer)** (running on any Windows, Linux, or macOS computer on your local network) to query and control monitor/display hardware settings remotely.

---

## 🌟 Key Features

- **Cross-Platform Host Support**: Connects to [MultiDisplayVCP Server v2.0+](https://github.com/doggdogpack/MultiDisplayVCPServer) instances running on Windows, Linux, or macOS.
- **Universal Macro Deck 3 Plugin**: A single package with native binaries for Windows (`win-x64`), Linux (`linux-x64`), and macOS (`osx-arm64` & `osx-x64`).
- **Dynamic Variable Catalog**: Automatically registers reactive Macro Deck variables for every monitor and supported VCP feature (`vars.multidisplay_<connection>_<monitor>_<feature>`).
- **High-Performance Dual Networking**:
  - **MagicOnion / gRPC (HTTP/2)**: Fast, zero-allocation protocol.
  - **TCP Fallback**: Seamless fallback to standard TCP sockets for network flexibility.
- **HMAC-SHA256 Authentication**: Challenge-based security with replay-attack protection.
- **Mobile Control Surface**: Stream live monitor states and control displays from your phone or tablet running the Macro Deck mobile app (iOS / Android).

---

## 📦 Installation

1. Make sure you have **[Macro Deck 3](https://macro-deck.app/)** installed.
2. Download the latest `com.multidisplayvcp.client-3.0.0.macroDeckPlugin` from the [Releases](https://github.com/doggdogpack/MultiDisplayVCPClient/releases) tab.
3. Drag and drop the `.macroDeckPlugin` file into the Macro Deck 3 window (or install it via Macro Deck's Extension Manager).
4. Run the **[MultiDisplayVCP Server](https://github.com/doggdogpack/MultiDisplayVCPServer/releases)** on each computer whose displays you want to manage.
5. In Macro Deck, open the plugin settings and configure your server connection(s) (Host IP, Port, and Password).

---

## 🗂️ Branches & Compatibility

> [!NOTE]
> **Unified Server**: The [MultiDisplayVCP Server](https://github.com/doggdogpack/MultiDisplayVCPServer) is a single unified application (no split branches). Running **Server v2.0.0+** concurrently supports both Macro Deck 2 and Macro Deck 3 clients across Windows, Linux, and macOS.

| Macro Deck Version | Client Plugin Branch | Supported Server | Status |
| :--- | :--- | :--- | :--- |
| **Macro Deck 3.x** | [`Macro-Deck-3`](https://github.com/doggdogpack/MultiDisplayVCPClient/tree/Macro-Deck-3) (v3.0.0+) | [MultiDisplayVCP Server v2.0+](https://github.com/doggdogpack/MultiDisplayVCPServer) (gRPC / HTTP/2 + TCP) | **Active (Current)** |
| **Macro Deck 2.x** | [`Macro-Deck-2`](https://github.com/doggdogpack/MultiDisplayVCPClient/tree/Macro-Deck-2) (v2.0.0) | [MultiDisplayVCP Server v2.0+](https://github.com/doggdogpack/MultiDisplayVCPServer) (TCP) or v1.x | **Legacy (Archived)** |

---

## 🛠️ Building from Source

Requires [.NET 10.0 SDK](https://dotnet.microsoft.com/) and the `macrodeck-plugin` CLI:

```bash
# Build multi-platform package
macrodeck-plugin build
```

The output package will be generated at `BuildOut/Client/Packaged/com.multidisplayvcp.client-3.0.0.macroDeckPlugin`.

---

## 📄 License

MIT License. See [LICENSE.txt](LICENSE.txt) for details.
