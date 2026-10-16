# Techbase branding

The logo was supplied by the user for branding this application:
https://www.techbasebv.nl/wp-content/uploads/2026/04/hdb-techbase-logo-hor-light-2.png

`TechbaseLogo.png` is the original, unchanged PNG used in the application header. `TechbaseIcon.png` and `Techbase.ico` export its symbol with a transparent background and a single dark-orange ink, `#FF5A00`. The icon is used by the executable, installer, windows and tray. The logo and trademark remain the property of their owner; this repository does not grant separate rights to them.

Run `./Build-Icon.ps1` on Windows to reproduce the icon. The script uses the original symbol's alpha and outline, crops out the wordmark and exports nine real 32-bit RGBA PNG frames (16–256 px) inside the ICO. No opaque background or palette quantisation; antialiasing changes coverage only, not the orange ink. `TechbaseIcon.png` is the 256 px preview. Generated artwork drafts were not selected: the final export retains the original supplied logo shape.

Palette: navy `#15253A`, orange `#FF7900`, pale background `#F3F5F8`. Assets are embedded in the Windows application and work offline; the application never downloads branding at runtime.
