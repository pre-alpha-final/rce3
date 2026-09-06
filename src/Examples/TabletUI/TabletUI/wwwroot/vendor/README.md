# Bundled Sketchy assets

The app loads these assets locally; it does not request a CDN or Google Fonts at runtime.

- Bootswatch Sketchy 5.3.8: https://cdn.jsdelivr.net/npm/bootswatch@5.3.8/dist/sketchy/bootstrap.min.css
  - MIT license: sketchy/LICENSE.bootswatch
  - Includes Bootstrap 5.3.8, MIT license: sketchy/LICENSE.bootstrap
  - Local modification: removed the Google Fonts @import and source-map reference. Upstream line endings are preserved.
- Neucha regular: https://github.com/google/fonts/tree/main/ofl/neucha
  - SIL Open Font License 1.1: fonts/LICENSE.neucha.txt
- Cabin Sketch regular and bold: https://github.com/google/fonts/tree/main/ofl/cabinsketch
  - SIL Open Font License 1.1: fonts/LICENSE.cabin-sketch.txt

Font binaries and complete upstream licenses were retrieved on 2026-09-06.
fonts/fonts.css declares the locally bundled TTF files. No Bootstrap JavaScript is needed.
The app icon in ../icon.svg and its PNG variants are original geometric artwork.
