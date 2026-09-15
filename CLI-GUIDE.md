# HueReka CLI — Quick Start

Control individual Philips Hue lights from a normal Windows Command Prompt.

## 1. Install the executable

Use Windows 10/11 with .NET Framework 4.7.2 or newer. Connect the PC and Hue Bridge to the same local network.

HueReka is portable: only **huereka.exe** is needed. There is no installer, and administrator access is not required.

1. In File Explorer, open `C:\Repos\HueReka!\dist` and copy **huereka.exe**.
2. Enter `%LOCALAPPDATA%` in File Explorer's address bar. Create a folder named **HueReka** and paste the executable into it.
3. Open **Command Prompt** and run:

```bat
cd /d "%LOCALAPPDATA%\HueReka"
huereka help
```

For updates, replace this copy of `huereka.exe` with the newer version. Your saved pairing is stored separately.

**Optional — run from any folder:** Search Windows for **Edit environment variables for your account**. Under **User variables**, edit **Path**, click **New**, and add `%LOCALAPPDATA%\HueReka`. Save and open a new Command Prompt. You can then type `huereka` from any folder.

## 2. Pair with the bridge

Skip this step if you have already paired using the desktop app under the same Windows account.

Find the bridge's IP address in the Hue app's bridge settings, then run:

```bat
huereka pair 192.168.1.20
```

Replace `192.168.1.20` with your bridge's IP address. When prompted, press the round link button on the bridge; the command waits up to 90 seconds and finishes as soon as the button is pressed. Pairing is remembered for your Windows account.

## 3. Find and control a light

List the available lights:

```bat
huereka list
```

Use the **ID** column in your commands. These examples control light `1`:

| Command | Action |
| --- | --- |
| `huereka on 1` | Turn on |
| `huereka off 1` | Turn off |
| `huereka brightness 1 50` | Set brightness to 50% and turn on |
| `huereka color 1 FF8800` | Set orange color and brightness, and turn on |
| `huereka warm 1` | Set warm white and turn on |
| `huereka cool 1` | Set cool white and turn on |

Brightness accepts whole numbers from **1 to 100**. Use `off` to switch off. Warm/cool and dimming require a compatible light. Each command affects only the specified light.

**HTML colors:** `huereka color 1 RRGGBB` accepts exactly six uppercase hexadecimal digits, without `#`. Examples: `FF0000` (red), `00FF00` (green), `0000FF` (blue), `FFFFFF` (white). The command sets both color and brightness from the RGB value; `000000` turns the light off. A color-capable bulb is required. Actual colors depend on the bulb and may differ from a screen.

## Help and troubleshooting

- **All commands:** Run `huereka help`.
- **Unreachable light:** Check its physical power switch and connection to the bridge.
- **Cannot connect:** Check the network and bridge IP. If the address changed, pair again using the new address.
- **Command not recognized:** Change to the installation folder or add it to your user Path as described above.
- **Batch scripts:** Run `echo %ERRORLEVEL%` immediately after a command: **0** = success, **1** = connection/bridge/credential error, **2** = invalid command, light ID, or unsupported capability.
