
[![Release][release-shield]][release-url]

[//]: # ([![Discord][discord-shield]][discord-invite-url])
![Build Status][build-shield]
![License](https://img.shields.io/github/license/marko-pilipovicc/DCS-CDU-OCR-Bridge)

# DCS-CDU-OCR-BRIDGE

This console application bridges DCS World with the Winwing MCDU hardware, enabling real-time data exchange between the simulator and the physical device.

**Data Flow:**
- **Standard Flow:** DCS ⟷ DCS-BIOS ⟷ This App ⟷ Winwing hardware
- **C-130J OCR Flow:** DCS (Screen Capture) ⟶ OCR Processing ⟶ This App ⟶ Winwing hardware

## Quick Start

1. **Install DCS-BIOS** (see detailed instructions below)
2. **Download and extract** this application to your preferred folder
3. **Connect** your Winwing devices (before starting bridge)
4. **Run** the application
5. **Launch DCS** and select your aircraft from the MCDU menu

## Requirements

- DCS World
- DCS-BIOS (v0.8.4 or later, nightly build required for CH-47F)
- .NET 8.0 runtime
At least one of these devices.
- Winwing CDU hardware (MCDU / PFP3N / PFP7)
- Winwing FCU and EFIS ( tested with Left Efis )


## Supported Aircraft

| Aircraft | Support Level | Features |
|----------|---------------|----------|
| **A10C** | Full | Complete MCDU functionality, LED indicators, brightness control , FCU display (VS , Alt, Speed, HDG , Qnh on Efis ) |
| **AH-64D** | Basic | UFD information, keyboard display |
| **FA-18C** | Basic | UFC fields display |
| **CH-47F** | Basic | Pilot or CoPilot CDU (requires DCS-BIOS nightly build) |
| **F15E** | Basic | UFC Lines 1-6 by smreki |
| **M2K** | Basic | see documentation in docs/ |
| **C-130J** | Advanced | CNI display via OCR |

### LED Mappings (A10C)

| MCDU LED | DCS Indicator |
|----------|---------------|
| Fail | Master Caution |
| FM1 | Gun Ready |
| IND | NWS Indicator |
| FM2 | Cockpit Indicator |

### LED Mappings other aircraft
| MCDU LED | DCS Indicator |
|----------|---------------|
| Fail | Master Caution (CH-47F) |

## OCR Support (C-130J)

The C-130J implementation uses Optical Character Recognition (OCR) to extract data from the DCS screen, as certain display data is not yet available via DCS-BIOS.

- **Requirement:** The CNI display must be visible on your screen for the OCR to capture it.
- **Configuration:** Viewport coordinates can be adjusted in `Config/OCR/profiles/C-130J/PILOT_CNI.json`.
- **Performance:** Runs at approximately 5 FPS to minimize CPU impact.
- **LED Mappings:** Currently not implemented due to lack of support in DCS-BIOS.

## Installation

### DCS-BIOS Setup

1. **Download** the latest DCS-BIOS release:
   - Standard: https://github.com/DCS-Skunkworks/dcs-bios/releases
   - For CH-47F: Download nightly build (or later than 0.8.4)

2. **Extract** the DCS-BIOS folder to your DCS saved games Scripts directory:
   ```
   %USERPROFILE%\Saved Games\DCS\Scripts\DCS-BIOS\
   ```

3. **Configure Export.lua** in your Scripts folder:
   ```lua
   dofile(lfs.writedir() .. [[Scripts\DCS-BIOS\BIOS.lua]])
   ```
   
   ⚠️ **Important:** If you already have an Export.lua file, add the line above instead of overwriting it.

### Application Setup

1. **Extract** the application files to your chosen directory
2. **Run** `WWCduDcsBiosBridge.exe`
if no config.json is found, it will create a default one and show you a dialog box to edit it.

<img width="441" height="368" alt="image" src="https://github.com/user-attachments/assets/dca3d830-970d-4741-aeb5-7358658f82f0" />

⚠️ **Important:** When updating the application, do not overwrite your existing `config.json` file.

## Usage

### Controls

- **MCDU Keys:** Map them in DCS.
- **Aircraft Selection:** Use line select keys on startup screen

## Utilities

### Font Editor

A Python-based GUI for editing CDU fonts is available in `utilities/font_editor.py`. It allows you to:
- Interactively toggle pixels on a 21x31 grid.
- Load and save directly to `Resources/*.json` files.
- Support for both Large and Small glyphs.

*Requires Python installed on your system.*

## Troubleshooting

### Common Issues

**"PLT_CDU_LINE1" does not exist (CH-47 Chinook)**
- Wrong dcsbios version installed.
- You need a version later than 0.8.4 (not including 0.8.4 itself)
  
**"Connection failed" or MCDU not responding**
- Ensure your Winwing MCDU is properly connected
- Try unplugging and reconnecting the device
- Check that no other applications are using the MCDU

**"No data appearing on MCDU"**
- Start your aircraft in DCS (data appears after aircraft systems are powered)
- Check that DCS-BIOS is working (look for network traffic)
- Verify Export.lua is configured correctly

**Aircraft change not working**
- Restart the application when switching aircraft
- Each aircraft requires a separate application instance

**Start bridge is greyed**
- You are using PFP4 not yet supported?

Otherwise, 
- You probably launched the app before plugging your devices.
- Exit application, plug all the cdus you plan to use and launch the app again 

### Brightness Issues

- **Mismatched brightness:** Use the aircraft's brightness controls first, then adjust MCDU
- **A10C:** MCDU brightness is linked to the console rotary control (right pedestal)
- **CH-47F:** Check the [specific documentation](docs/CH-47.md)
- In case of flickering with SimAppPro running, check the

<img width="50%" alt="image" src="https://github.com/user-attachments/assets/1cc6f86f-8fc8-457e-a9fb-11191fcd966d" />

### Logs

All application activity is logged to `log.txt` in the same folder as the executable. Check this file for detailed error information.

Report issues [here](https://github.com/marko-pilipovicc/DCS-CDU-OCR-Bridge/issues), or reach out on Discord [![Discord][discord-shield]][discord-invite-url].

## Known Limitations

- **Aircraft switching:** Requires application restart
- **Cursor behavior:** May appear erratic during waypoint entry (reflects DCS-BIOS data)
- **CH-47F support:** Requires DCS-BIOS nightly build (later than 0.8.4 )
- **C-130J OCR:** Requires the CNI display to be visible on screen; works best in Windowed or Borderless Windowed mode.
- **C-130J LED Mappings:** Currently not implemented due to lack of support in DCS-BIOS.
- **Brightness sync:** May not perfectly match aircraft state

## Development

This project is written in C# and targets .NET 8.0. It uses:
- **DCS-BIOS** for DCS communication
- **mcdu-dotnet** for MCDU hardware interface
- **DcsOcr** (submodule) for OCR-based data extraction
- **OpenCvSharp & OnnxRuntime** for OCR processing
- **NLog** for logging
- **System.CommandLine** for command-line parsing

## Contributing
see `docs/CONTRIBUTING.md` for contribution guidelines. [link](docs/CONTRIBUTING.md)

## License

See `LICENSE.txt` and `thirdparty-licences.txt` for licensing information.

## Support

For issues and questions, please check the logs first and review the troubleshooting section above.

[release-url]: https://github.com/marko-pilipovicc/DCS-CDU-OCR-Bridge/releases
[release-shield]:  https://img.shields.io/github/release/marko-pilipovicc/DCS-CDU-OCR-Bridge.svg

[dcs-forum-discussion]: https://forum.dcs.world/topic/368056-winwing-mcdu-can-it-be-used-in-dcs-for-other-aircraft/page/4/
[build-shield]: https://img.shields.io/github/actions/workflow/status/marko-pilipovicc/DCS-CDU-OCR-Bridge/build-on-tag.yml
