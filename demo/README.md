# Demo

`fmdemo` loads feature models written in JSON, YAML, UVL and Clafer (one file per scope), resolves
capabilities across them, and runs the solver-backed analyses. The sample system is the phone example
from the paper: Messaging, Connectivity, Keyboard and Storage, each owned by its own scope and linked
only by capabilities (`requires` / `provides`).

## Start here: the interactive explorer

```bash
dotnet run --project demo/CapabilityResolver.Demo -- explore demo/phone/mixed
```

(needs the .NET 10 SDK, run from the repository root). You get every scope's feature tree on one screen,
with numbered features, and the resolver's verdict underneath:

- **Type a number** to pick or unpick a feature (`3`, or several: `3 8 12`). The screen redraws.
- Markers: `[x]` picked by you, `[+]` selected because its own scope forces it (mandatory parts, the only
  option left), `[ ]` not selected, `[-]` no longer possible given your other picks in that scope.
- **Each scope on its own** shows whether that model is valid by itself.
- **Capabilities** shows what scopes require from each other: satisfied, or MISSING together with the one
  scope that would be told, because only that scope can provide it.
- **Global rules** shows the direct rules between scopes (here: secure and insecure storage cannot coexist).
- **RESULT** is VALID only when every scope is valid, every required capability is provided and no rule is violated.

Other commands inside the explorer: `scenarios` and `load missing-internet` (ready-made selections),
`suggest InternetConnectivity` then `apply` (cheapest features that provide a capability), `save my.json`,
`reset`, `help`, `quit`.

A good first session on `demo/phone/mixed`:

1. Type `3 8 10 12 14` (QWERTY, Email, Comic Email, Mobile Data, Legacy Storage). Email's need for internet is
   met by Mobile Data, but Comic Email needs emoji rendering and nothing provides it yet: the resolver shows
   `EmojiRendering` as MISSING and says only the Keyboard scope would be told. Type `6` (Emoji Support) and it turns VALID.
2. Type `6` again to unpick it, then `suggest EmojiRendering` and `apply`: the resolver finds the same fix itself.
3. Type `load encrypted-friction` to see a global rule violation, and `4` to see a pick refused.

The same works in every directory below; only the language of the scope files differs.

## Other commands (non-interactive)

Run from the repository root:

```bash
dotnet run --project demo/CapabilityResolver.Demo -- <command> <dir> [options]
```

## Demo files

| Directory | Contents |
|---|---|
| `phone/json` | the four scopes as `*.fm.json` |
| `phone/yaml` | the same scopes as `*.fm.yaml` |
| `phone/uvl` | the same scopes as `*.uvl` (official UVL grammar) |
| `phone/clafer` | the same scopes as `phone.<scope>.cfr` (model id is the file name) |
| `phone/mixed` | one scope per language: Messaging in UVL, Connectivity in Clafer, Keyboard in YAML, Storage in JSON |
| `configs/` | selections to try (see below) |
| `broken/` | one invalid file per language, to see the error messages |

Every directory also has `rules.json` or `rules.yaml` with the global rule
`SecureStorage ≠ InsecureStorage`. Every directory describes the same system, so every configuration
gives the same answer in every language.

## Command reference

```bash
# What was loaded, and from which language (the explorer shows this too)
dotnet run --project demo/CapabilityResolver.Demo -- models demo/phone/mixed

# Resolve a configuration
dotnet run --project demo/CapabilityResolver.Demo -- check demo/phone/uvl --config demo/configs/comic-email.json

# Can a partial selection be completed? What does it force in other scopes?
dotnet run --project demo/CapabilityResolver.Demo -- complete demo/phone/json --config demo/configs/comic-email.json

# Cheapest way to provide a capability
dotnet run --project demo/CapabilityResolver.Demo -- suggest demo/phone/clafer --config demo/configs/no-emoji.json --capability EmojiRendering

# Satisfiability, core and dead features, capability catalog
dotnet run --project demo/CapabilityResolver.Demo -- analyze demo/phone/yaml
```

`check` completes each scope's selection from that scope's own model first (mandatory parents and
children, forced choices), so a configuration lists only the choices. `--exact` skips that and resolves
the selection exactly as written. Exit code: 0 valid, 1 invalid, 2 usage or file error.

## Scenarios

| Configuration | What it shows | Try |
|---|---|---|
| `configs/comic-email.json` | everything satisfied | `check` : VALID |
| `configs/missing-internet.json` | `InternetConnectivity` is required but Connectivity picked nothing; only the Connectivity scope is told | `check`, then `suggest --capability InternetConnectivity` |
| `configs/no-emoji.json` | Comic Email needs `EmojiRendering`, but the Keyboard scope picked AZERTY without Emoji Support | `check`, then `suggest --capability EmojiRendering` |
| `configs/encrypted-friction.yaml` | Encrypted Messaging is satisfied, but Legacy Storage is still selected and the global rule forbids both storage capabilities | `check` : global rule VIOLATED |
| `configs/contradiction.json` | two keyboard layouts at once | `check` and `complete` : cannot be completed |

A configuration file maps model ids to the features chosen by hand (JSON or YAML):

```json
{ "phone.messaging": ["Email", "ComicEmail"], "phone.connectivity": ["MobileData"] }
```

## Try your own model

Copy one scope file, change it, and run `models` on the directory to see how it was read. Capabilities are
written per language:

| Language | File | Capabilities |
|---|---|---|
| JSON / YAML | `*.fm.json`, `*.fm.yaml` | `"requires": [...]`, `"provides": [...]` on a feature |
| UVL | `*.uvl` | `Feature {requires ['x'], provides ['y']}` |
| Clafer | `*.cfr` | sub-clafers `requires -> string = "x"` and `provides -> string = "y"` |

Constraints stay local to a scope and may only mention that scope's features. Links between scopes
exist only as capabilities, so a UVL `imports` block is rejected (see `broken/imports.uvl`).
Scope ids must be unique across the directory: the `namespace` in UVL, the `id` in JSON/YAML, the file
name in Clafer.
