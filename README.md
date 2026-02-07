# IPTV Player for Windows

A modern Windows desktop application for playing IPTV streams using M3U playlist files.

![.NET](https://img.shields.io/badge/.NET-8.0-blue)
![Platform](https://img.shields.io/badge/Platform-Windows-blue)
![License](https://img.shields.io/badge/License-MIT-green)

## Features

- 📺 **M3U/M3U8 Playlist Support** - Load IPTV playlists from local files or URLs
- 🎬 **VLC-Powered Playback** - Supports a wide range of streaming formats (HLS, RTSP, RTMP, etc.)
- 🔍 **Channel Search** - Quickly find channels by name or group
- 📱 **Modern Dark UI** - Clean, modern interface with dark theme
- ⌨️ **Keyboard Shortcuts** - Full keyboard control for convenient navigation
- 🔊 **Volume Control** - Adjustable volume with mute option
- 📺 **Fullscreen Mode** - Watch in fullscreen for better viewing experience

## System Requirements

- Windows 10 or later (64-bit)
- .NET 8.0 Runtime
- 2 GB RAM minimum
- Hardware video acceleration recommended

## Installation

### Prerequisites

1. Install [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (for building)
2. Or install [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0/runtime) (for running only)

### Build from Source

```bash
# Clone or download the repository
cd IPTV-Windows

# Restore packages and build
dotnet restore
dotnet build --configuration Release

# Run the application
dotnet run
```

### Create Executable

```bash
# Create a self-contained executable
dotnet publish -c Release -r win-x64 --self-contained true -o ./publish
```

## Usage

### Loading a Playlist

1. Click the **"📂 Open Playlist"** button
2. Select an M3U or M3U8 file from your computer
3. The channel list will populate on the left panel

### Playing Channels

- **Double-click** a channel to start playing
- Use **Page Up/Down** to switch between channels
- Use the **playback controls** at the bottom of the window

### Keyboard Shortcuts

| Key | Action |
|-----|--------|
| `Space` | Play/Pause |
| `F` or `F11` | Toggle Fullscreen |
| `Escape` | Exit Fullscreen |
| `M` | Mute/Unmute |
| `Page Up` | Previous Channel |
| `Page Down` | Next Channel |
| `Ctrl + Up` | Volume Up |
| `Ctrl + Down` | Volume Down |

## M3U Playlist Format

The application supports standard M3U/M3U8 playlist format:

```m3u
#EXTM3U
#EXTINF:-1 tvg-id="channel1" tvg-name="Channel 1" tvg-logo="http://example.com/logo.png" group-title="News",Channel 1 HD
http://example.com/stream1.m3u8

#EXTINF:-1 tvg-id="channel2" tvg-name="Channel 2" group-title="Sports",Channel 2
http://example.com/stream2.m3u8
```

### Supported Attributes

- `tvg-id` - Channel ID for EPG
- `tvg-name` - Channel name
- `tvg-logo` - Channel logo URL
- `group-title` - Channel category/group
- `tvg-language` - Channel language
- `tvg-country` - Channel country

## Supported Stream Formats

Thanks to LibVLC, the player supports many streaming protocols:

- **HLS** (.m3u8)
- **RTSP** (rtsp://)
- **RTMP** (rtmp://)
- **HTTP/HTTPS** streams
- **UDP** multicast
- Most common video codecs (H.264, H.265, VP9, etc.)

## Project Structure

```
IPTV-Windows/
├── Models/
│   ├── Channel.cs          # Channel model
│   └── ChannelGroup.cs     # Channel group model
├── ViewModels/
│   └── MainViewModel.cs    # Main view model (MVVM)
├── Views/
│   ├── MainWindow.xaml     # Main window UI
│   └── MainWindow.xaml.cs  # Window code-behind
├── Services/
│   └── M3UParser.cs        # M3U playlist parser
├── Converters/
│   └── Converters.cs       # WPF value converters
├── Styles/
│   └── AppStyles.xaml      # UI styles and themes
├── Resources/              # Application resources
├── App.xaml                # Application entry point
└── IPTV-Player.csproj      # Project file
```

## Dependencies

- [LibVLCSharp](https://github.com/videolan/libvlcsharp) - VLC media player bindings for .NET
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) - MVVM framework

## Troubleshooting

### Playback Issues

- **"Error playing channel"** - The stream URL may be invalid or the server is down
- **Black screen** - Some streams require specific codecs; try a different channel
- **Buffering** - Check your internet connection speed

### Build Issues

- Make sure .NET 8.0 SDK is installed
- Run `dotnet restore` before building
- On first run, VLC libraries will be downloaded automatically

## License

This project is open source. Feel free to use, modify, and distribute.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.
