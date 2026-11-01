# RetailOps design direction

The owner delegated the product theme, technology choices, and appearance. This direction serves a purchasing team that spends time reading SKU lists and reconciling quantities.

## Identity

Product name: RetailOps. Display the name as a typographic wordmark; do not invent a pictorial logo, customer logos, or employee portraits. The interface should resemble a well-organized operations desk: legible, deliberate, practical, with a clear place for each document and action.

## Palette and type

Use warm paper (#f5f4ef) for the main work area, white surfaces, deep forest (#163b32) for navigation and primary actions, dark ink (#172b26) for text, and burnt orange (#a23f1f) for attention requiring action. Muted text is #52615a. Status colors must always accompany written labels. No gradients or glow are needed for these operational tasks.

Use a locally bundled sans-serif such as IBM Plex Sans for its clear numerals and compact data tables. Use tabular numbers for quantities and monetary values. Serif display type is unnecessary. Product identity comes from editorial hierarchy, forest navigation, warm surfaces, and a compact document reference treatment.

## Layout and behavior

Desktop: fixed-width left navigation, compact workspace identity and user menu, then a spacious main panel. Overview uses a prominent low-stock/action section alongside a concise movement chart; it must not become a wall of equal statistic cards. Lists use tables, filter bars, and pagination. Details use a document header, status history, line items, and explicit next actions.

Mobile: navigation opens from a labeled menu button. Filters stack; dense tables may scroll within their labeled region, never the viewport. Primary form actions remain reachable. Dialogs use native focus management, close on Escape, and restore trigger focus. All controls have a minimum 44px target and visible keyboard focus.

Loading, empty, and error states use text and a useful action. No invented operational metrics appear before data exists. Motion is limited to a short panel transition or pending indicator, respecting reduced motion. Icons, if used, must identify the actual destination or action; text labels remain visible.

Design read: ENERGY 2 / RHYTHM 3 / MOTION 1. Tables are intentionally dense enough for operators, while setup and sign-in remain calm and focused. Contrast must be measured and actual desktop/mobile screenshots reviewed before release.

## Retail workstation extension

RetailOps extends the purchasing direction above into a branch retail workstation. The owner previously chose a light working UI and approved POS/offline/accounting scope. No new illustration, logo or asset library is needed; reuse the bundled font with its license. One clear focal point per view: product search/cart for POS, queue outcomes for synchronization, document evidence for receipts, and financial statements for accounting.

Keep branch, price tier, shift and synchronization state visible before checkout. On desktop separate products and cart; mobile must have a deliberate stacked/cart interaction. Offline is a real connection/queue state with text, not a decorative badge. Cache age and last server stock explain uncertainty. Pending local commands are not confirmed sales. Errors must be actionable and preserve entered work. Financial reports explain their date and organization-wide scope. Empty screens have no invented records or claims.

Root personally reviews final desktop/mobile screenshots and writes the four-block Delivery Gate after verification. Respect 44px targets, visible focus, reduced motion, wrapping identifiers and internal table scrolling. Warm paper/forest carry identity; amber/red represent pending/conflicts and green server-confirmed results, always accompanied by words.
