# DevWorkspace website

`dist/` is a buildless, responsive Turkish/English corporate product website. It uses a conceptual CSS diagram rather than application screenshots. The expanded homepage links to a searchable catalog of 76 detailed feature explanations and 12 standalone module pages. Each module includes practical usage, supported operations and scope/requirements. It also includes a keyboard-accessible capability explorer, development workflow, use cases, supported project ecosystems, privacy context, resources, FAQ and installer/portable downloads. Turkish is the default; an explicit language choice is retained locally across pages. No third-party runtime requests, analytics, scripts or build dependencies are required. Download links point to the existing public GitHub latest release; this does not publish a new desktop release.

Run `node website/preview.mjs` from the repository root, then open `http://127.0.0.1:4173`. Alternatively serve `dist/` with any static HTTP server. `.openai/hosting.json` retains the private Sites project identity. To publish publicly, explicitly change the Site audience or deploy these static files using your preferred hosting. Do not claim WinGet availability until the package is accepted. Website changes do not require a desktop version bump, desktop publish or desktop release-note changes.

Run `node website/build-guides.mjs` from the repository root after editing `docs/privacy.md`, `CONTRIBUTING.md`, `docs/development-hub.md` or `docs/workspace-tools.tr.md` to regenerate the bundled guide pages.

Edit `website/product-data.mjs` for bilingual feature content, then run `node website/build-product.mjs`. This regenerates `features.html`, the `module-*.html` pages and marked product sections in `index.html`. Keep claims aligned with current application source: scan and repair coverage differ; move planning does not move files; Git recovery cannot restore unpushed local work; project detection does not guarantee run/build support. No desktop binaries or release notes are changed by this website workflow.

`user-guide.html` is the task-oriented, bilingual general usage manual, with 14 chapters, a contents sidebar, first-day navigation, print styles and Turkish/English plain-text downloads. Edit `user-guide-data.mjs` for instructions and `user-guide-content.mjs` for its structure, then rerun `node website/build-product.mjs`. `user-guide.js` follows the current language for the downloadable document and opens the browser print dialog on request; PDF saving is performed by the visitor's browser.



`light-theme.css` is the single cream, sage and dark-green design system shared by all 19 HTML pages, including the homepage opening. Typography uses system Segoe UI fonts. Headers, buttons, cards, catalog filters, document notes and download sections follow the same palette and restrained geometry. The product generator applies this stylesheet last; there are no premium, gold/navy or homepage-only style layers.
