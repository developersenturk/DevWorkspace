# DevWorkspace website

`dist/` is a buildless, responsive English/Turkish product page with a real application screenshot. No external fonts, analytics, scripts or build dependencies are required. Download links point to the existing public GitHub latest release; this does not publish a new desktop release.

Serve `dist/` with any static HTTP server to preview. `.openai/hosting.json` retains the private Sites project identity. To publish publicly, explicitly change the Site audience or deploy these static files using your preferred hosting. Do not claim WinGet availability until the package is accepted.

Run `node website/build-guides.mjs` from the repository root after editing the privacy or contribution guide to regenerate their static pages.
