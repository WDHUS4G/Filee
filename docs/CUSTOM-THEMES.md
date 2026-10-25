# Custom themes

Filee's own colours can be replaced with a `theme.json` file in the folder that holds `settings.json`:

- Windows: `%APPDATA%\Filee\theme.json`
- Linux: `~/.config/Filee/theme.json`
- macOS: `~/Library/Application Support/Filee/theme.json`
- With `FILEE_DATA_DIR` set: `theme.json` in that folder

```jsonc
{
  "dark": {
    "page": "#101820",          // window background
    "surface": "#18222C",       // cards and donut slices
    "surfaceAlt": "#22303C",    // badges, small buttons, preview boxes
    "border": "#1CFFFFFF",      // card and control outlines
    "text": "#E8E4D8",
    "textSecondary": "#A8B0B8", // hints and captions
    "danger": "#F09595",
    "success": "#5DCAA5",
    "warning": "#EF9F27"
  },
  "light": {
    "page": "#F4F6F8"
  }
}
```

- `dark` and `light` are both optional, and so is every colour in them. Anything left out keeps Filee's colour.
- Colours are `#RRGGBB` or `#AARRGGBB`. Unknown names and other values are ignored and written to the log, and so is
  a file with no usable colour (for example `"Dark"` with a capital D) or one over 64 KB.
- The accent colour, corner radius and light/dark choice stay in **Settings > Theme**.
- Filee reads the file when it starts and whenever a setting is saved. Restart Filee, or change any setting, after
  editing it. Deleting the file brings the built-in colours back.

Keep `text` and `textSecondary` readable on `page`, `surface` and `surfaceAlt`: they are drawn on all three.

Keep `dark` colours dark and `light` colours light. In Filee's default Soft style, text boxes,
drop-downs, check boxes, menus, tooltips and plain buttons come from Avalonia's Fluent theme and
keep its standard light/dark look; they do not follow `theme.json`. Under the Material You style,
text boxes, drop-downs and check boxes follow the dynamic tonal surface colors and corner radii
from `theme.json` and the accent colour. A white `page` in the `dark` variant would put white-on-white
controls on it.
