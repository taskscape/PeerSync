# PeerSync homepage

A fully static marketing site for PeerSync. No build step, no external
requests: open `index.html` directly from disk or serve the folder with any
static web server.

| File | Purpose |
| --- | --- |
| `index.html` | Homepage: hero, feature grid, setup walkthrough, security model, CLI, install call to action |
| `use-cases.html` | Six deployment scenarios with setup recipes, a comparison table and an honest "not a fit yet" list |
| `contact.html` | Static contact form. Validation runs in the browser; sending opens the visitor's mail client with the message prefilled |
| `css/styles.css` | All styling, including the `@font-face` rules |
| `js/main.js` | Navigation toggle, scroll reveal, copy buttons, step tabs, form handling |
| `fonts/` | Inter and JetBrains Mono variable fonts (`woff2`, Latin subset) with their SIL Open Font License texts |
| `assets/favicon.svg` | Site icon |

## Things to adjust before publishing

- The contact form's destination address lives in the `data-mailto`
  attribute of the `<form>` in `contact.html`.
- Download buttons point at the GitHub Releases page; documentation links
  point at the files in this repository on GitHub.
- The sample folder keys and device IDs on the pages are illustrative and
  not real keys.
