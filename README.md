# Cpp2IL - Enhanced

```
░░░░░░  ░░░░░░░ ░░░░░░░░ ░░░░░░░░ ░░░░░░░ ░░░░░░   ░░░░░░ ░░░░░░  ░░░░░░  ░░░░░░  ░░ ░░      
▒▒   ▒▒ ▒▒         ▒▒       ▒▒    ▒▒      ▒▒   ▒▒ ▒▒      ▒▒   ▒▒ ▒▒   ▒▒      ▒▒ ▒▒ ▒▒      
▒▒▒▒▒▒  ▒▒▒▒▒      ▒▒       ▒▒    ▒▒▒▒▒   ▒▒▒▒▒▒  ▒▒      ▒▒▒▒▒▒  ▒▒▒▒▒▒   ▒▒▒▒▒  ▒▒ ▒▒      
▓▓   ▓▓ ▓▓         ▓▓       ▓▓    ▓▓      ▓▓   ▓▓ ▓▓      ▓▓      ▓▓      ▓▓      ▓▓ ▓▓      
██████  ███████    ██       ██    ███████ ██   ██  ██████ ██      ██      ███████ ██ ███████ 

```

### Advanced IL2CPP Decompilation & Recovery Tool

Cpp2IL is a powerful tool that reverse-engineers Unity's IL2CPP build process back to
readable C# source code. It parses IL2CPP metadata and binaries, performs deep analysis
using instruction-set-independent representations, and reconstructs compilable C# with
type resolution, control flow recovery, property/event detection, null-check elimination,
and aggressive copy propagation.

---

## Features

- **C# Source Reconstruction** - Outputs compilable .cs files from IL2CPP binaries
- **Type Propagation** - Resolves local variable types through method analysis
- **Control Flow Recovery** - Reconstructs if/else, loops, and switch statements
- **Property & Event Detection** - Identifies get_/set_ methods as C# properties/events
- **Null-Check Elimination** - Removes Unity runtime null-check boilerplate
- **Copy Propagation** - Eliminates redundant local-to-local moves from phi destruction
- **Operator Resolution** - Converts op_Equality, op_Addition, etc. to C# operators
- **Multi-Platform** - Supports Windows, Linux, macOS x64/arm64, WebAssembly, and PS4

## Quick Start

```
Cpp2IL --game-path="C:\Path\To\Your\Game"
```

Or for APK files:
```
Cpp2IL --game-path="C:\Path\To\game.apk"
```

Output will be written to `cpp2il_out/` by default.

## Usage

### Basic Usage

```
Cpp2IL --game-path=<path> [--output-as=<format>] [--output-to=<dir>]
```

### Command Line Options

| Option | Example | Description |
|:------:|:-------:|:-----------:|
| `--game-path` | `C:\Path\To\Game` | Path to the game folder or APK. Required. |
| `--exe-name` | `TestGame` | Name of the game's exe if auto-detection fails |
| `--verbose` | | Enable verbose logging |
| `--output-as` | `csharp_source` | Output format (default: `csharp_source`) |
| `--output-to` | `out` | Output directory (default: `cpp2il_out`) |
| `--list-output-formats` | | List available output formats |
| `--list-processors` | | List available processing layers |

### Output Formats

| Format | Description |
|:------:|:-----------:|
| `csharp_source` | Reconstructed C# source code (recommended) |
| `dll_il_recovery` | Managed DLL with recovered IL (requires analysis) |
| `metadata` | Raw metadata dump |
| `mewedumped` | Mewed-style metadata dump |

## Project Structure

```
Cpp2IL/
├── src/                          # Source projects
│   ├── Cpp2IL/                   # CLI application
│   ├── Cpp2IL.Core/              # Core analysis library
│   ├── LibCpp2IL/                # IL2CPP metadata parser
│   ├── StableNameDotNet/         # Name stabilization
│   ├── WasmDisassembler/         # WebAssembly support
│   └── Cpp2IL.Plugin.*/          # Plugins
├── tests/                        # Test projects
│   ├── Cpp2IL.Core.Tests/
│   └── LibCpp2ILTests/
├── docs/                         # Documentation
└── TestFiles/                    # Test data
```

## Building

### Prerequisites

- .NET 10.0 SDK (or .NET 9.0 for older builds)
- .NET Framework 4.7.2 SDK (for legacy build)

### Build Commands

```bash
# Build all projects
dotnet build -c Release

# Build CLI only
dotnet build src/Cpp2IL/Cpp2IL.csproj -c Release

# Run tests
dotnet test -c Release
```

## Development

The decompilation pipeline works as follows:

1. **IL2CPP Parsing** (`LibCpp2IL`) - Reads metadata and binary files
2. **ISIL Translation** (`Cpp2IL.Core`) - Converts x86/ARM/WASM to instruction-set-independent language
3. **CFG Construction** - Builds control flow graphs with dominator analysis
4. **Method Analysis** - Performs stack analysis, simplification, and metadata application
5. **C# Generation** (`CSharpSourceOutputFormat`) - Outputs reconstructed C# source code

Key entry points:
- `MethodAnalysisContext.Analyze()` - Starts decompilation of a method
- `CSharpSourceOutputFormat` - Handles C# source output
- `Simplifier` - Runs optimization passes on ISIL

## CI/CD

Every push triggers GitHub Actions to build:
- Native builds for Windows, Linux, macOS (x64 + arm64)
- .NET Framework 4.7.2 build for Wine/Proton compatibility
- Full test suite

## Credits

### Original Author
- **Samboy063** - Original Cpp2IL creator and maintainer

### Contributors
- **OmegaNatiry3** - Enhanced decompilation, type propagation, copy propagation, and C# source reconstruction improvements

### Dependencies

Built with the following libraries (all MIT licensed unless noted):

- [iced](https://github.com/icedland/iced) - x86 disassembler
- [Disarm](https://github.com/SamboyCoding/Disarm) - ARM64 disassembler
- [AsmResolver](https://github.com/Washi1337/AsmResolver) - .NET assembly manipulation
- [AssetRipper.CIL](https://github.com/AssetRipper/AssetRipper.CIL) - IL stub generation
- [Pastel](https://github.com/silkfire/Pastel) - Console colors
- [CommandLineParser](https://github.com/commandlineparser/commandline) - CLI parsing
- [xUnit](https://github.com/xunit/xunit) - Testing (Apache 2.0 + MIT)

### Acknowledgments

Based on [Il2CppDumper](https://github.com/Perfare/Il2CppDumper) and
[Il2CppInspector](https://github.com/djkaty/Il2CppInspector/).
Thanks to myself for early support.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
