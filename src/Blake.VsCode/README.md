# Blake Language Support for Visual Studio Code

Language support for Blake, a C# superset with metaprogramming capabilities.

## Features

### Syntax Highlighting

- **Meta-blocks** (`@{| |}`) - Compile-time code blocks with background highlighting
- **Quasi-quotes** (backtick strings) - Output templates with proper escaping
- **Splices** (`@()`) - Compile-time interpolation within quasi-quotes
- **C# syntax** - Full C# syntax highlighting for both meta-blocks and regular code

### Background Highlighting

Meta-blocks are highlighted with a subtle yellow background to distinguish compile-time code from regular C# code.

**Key behaviors**:
- `@{|` and `|}` delimiters get the background color
- Meta-block content gets the background color
- Backtick delimiters (`` ` ``) get the background color
- Content inside quasi-quotes does NOT get the background (shows as C# code)
- `@()` splices get the background, even inside quasi-quotes (compile-time expressions)

Example:
```blake
@{|  ← Yellow background (meta-block delimiter)
    // ← Yellow background
    var schema = new[] { "Id", "Name", "Email" };

    foreach (var field in schema) {
        // ← Yellow background
        `   public string @(field) { get; set; }`
        ↑ Yellow background (backtick delimiter)
        //  ↑ C# syntax highlighting (no background)
        //                   ↑ Yellow background (splice)
        //                                     ↑ Yellow background (backtick delimiter)
    }
    // ← Yellow background
|}   ← Yellow background (meta-block delimiter)
```

You can disable background highlighting in your VS Code settings:

```json
{
  "blake.enableMetaBlockHighlighting": false
}
```

## Building the Extension

1. Install dependencies:
   ```bash
   npm install
   ```

2. Compile TypeScript:
   ```bash
   npm run compile
   ```

3. Package the extension:
   ```bash
   npx vsce package
   ```

## Language Syntax

### Meta-blocks

Meta-blocks contain C# code that executes at compile time:

```blake
@{|
    var count = 0;
    for (int i = 0; i < 5; i++) {
        `   public int Field@(count++) { get; set; }`
    }
|}
```

### Quasi-quotes

Backtick strings are output templates that emit to the generated file:

```blake
`public class @(className) { }`
```

### Splices

`@()` expressions are evaluated at compile time and their results are inserted:

```blake
@{|
    var type = "string";
    var name = "Username";
    `   public @(type) @(name) { get; set; }`
|}
```

## License

MIT
