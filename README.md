# Screensavers

Two quiet, dark Windows screensavers that tell the time in words. Everything is dim and moves
slowly to avoid burn-in.

| | |
|---|---|
| **Borealis** | Faint northern lights ripple over a mountain lake at night, with the time written on the water. |
| **Nocturne** | A dim moon in its real current phase over a sparse, twinkling sky. |

## Install

Download `Borealis.scr` or `Nocturne.scr` from the [latest release](../../releases/latest),
right-click it and choose **Install**. Windows SmartScreen may warn about an unsigned file the
first time.

## Build

Each folder has a `build.cmd` that compiles the `.cs` file with the C# compiler that ships with
Windows (.NET Framework 4). No SDK needed.

```
Borealis\build.cmd
Nocturne\build.cmd
```

Both accept the standard screensaver arguments (`/s` run, `/p` preview, `/c` settings) plus
`/shot <file.png>` to render a single 1920x1080 frame.

Nocturne's moon textures are derived from NASA's LRO imagery (public domain).
