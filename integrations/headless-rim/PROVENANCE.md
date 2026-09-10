# Headless test adapter

Adapted from https://github.com/IlyaChichkov/HeadlessRimPatch at
`d3c5539ff62c19e76ab8e5d1a268d1dca461e161`, GPL-3.0; license retained here.
The three C# source files and About metadata originate upstream.

Local changes: build against installed assemblies; remove upstream's unconditional
30 FPS cap only in batch mode; report patch exceptions; patch build-icon generation
before def resolution; skip resolution maintenance without a display; skip drawing
disposal only when neither drawing collection was allocated; acknowledge the hidden
loading-window presentation so native synchronous events and autosaves execute;
skip world-feature label updates that require GUI text initialization.
This mod is enabled only
in the separate headless test profile. It removes presentation paths, including
UI updates, and must be validated with native gameplay rather than assumed safe.
