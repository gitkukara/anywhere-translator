# UI icons

The default UI icon set is [Hugeicons](https://hugeicons.com), **Stroke Rounded**.
These 12 icons come from the MIT-licensed `@hugeicons/core-free-icons` package,
version 4.3.5. `manifest.json` records the upstream names and source hashes;
`LICENSE.md` contains the package's license.

The SVG path coordinates are retained verbatim. `Views/HugeIconData.cs` contains
the same paths as frozen WPF geometries. `Views/HugeIcon.cs` renders them on the
original 24 × 24 grid, with a uniform 1.5-unit stroke and rounded caps and joins.
No icon font, runtime download or SVG rendering dependency is required.

Use `HugeIcon` for interface actions and navigation. Match the action's meaning
first, then its shape; prefer this same family when adding icons. Toolbar icons
use an 18 × 18 viewport inside a 28 × 28 button. Keep localized tooltips and
accessible names on the containing buttons. Provider logos, application branding
and user-imported images remain separate assets.
