# Headless adapter source notice

The three C# source files and About metadata derive from
[IlyaChichkov/HeadlessRimPatch](https://github.com/IlyaChichkov/HeadlessRimPatch/tree/d3c5539ff62c19e76ab8e5d1a268d1dca461e161),
revision `d3c5539ff62c19e76ab8e5d1a268d1dca461e161`, under [GPL-3.0](LICENSE).

Local modifications build against installed assemblies and adapt presentation
suppression, loading callbacks and batch-mode frame timing for isolated tests.
The adapter is enabled only in the headless profile. Validate native gameplay
when changing its patches; presentation suppression also affects UI callbacks.
