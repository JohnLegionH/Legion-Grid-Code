# Phlox LSL Grammar Review (Static Audit)

Files reviewed:
- `OpenSim\Addons\Phlox\grammar\LSL.g4` (280 lines, full)
- `OpenSim\Addons\Phlox\InWorldz.Phlox\Types\SupportedEventList.cs` (405 lines, full)

## Summary of conformance flags

| Severity | Item | Where |
|---|---|---|
| OVER (bug) | `<<=` accepted as assignment | LSL.g4 lines 75, 131 |
| OVER (bug) | `>>=` accepted as assignment | LSL.g4 lines 75, 131 |
| UNDER | `quaternion` type alias missing | LSL.g4 lines 223-231 |
| UNDER (minor) | Hex prefix only `0x`, not `0X` | LSL.g4 line 267 |
| UNDER (minor) | Unknown `\?` escapes rejected; SL keeps them literal | LSL.g4 lines 256-263 |
| Latent parser bug | `assignmentStmt` allows unbalanced parens `LPAREN? … RPAREN?` | LSL.g4 line 75 |
| Grammar over-accept (semantic-enforced) | Any `ID` accepted as event name | LSL.g4 101-103 + SupportedEventList.cs 59-334 |
| InWorldz extension | `bot_update` event not in SL | SupportedEventList.cs line 301 |
| OK | No ternary `?:` | LSL.g4 (entire file) |
| OK | No `\|=`, `&=`, `^=` | LSL.g4 lines 75, 131 |
| OK | All 7 SL types accepted | LSL.g4 lines 223-231 |
| OK | All standard SL statement forms present | LSL.g4 lines 73-91 |
| OK | `<x,y,z>` and `<x,y,z,s>` literals | LSL.g4 lines 202-208 |
| OK | Decimal/hex ints, float exponents | LSL.g4 lines 265-279 |
| OK | `//` and `/* */` comments | LSL.g4 lines 248-254 |
| Runtime-only | Short-circuit semantics — not expressible in grammar | LSL.g4 line 135 |

## Supported event list (39 events) — vs SL canonical

Phlox supports the following events (from SupportedEventList.cs):

`at_rot_target, at_target, attach, changed, collision, collision_end, collision_start, control, dataserver, email, http_response, http_request, land_collision, land_collision_end, land_collision_start, link_message, listen, money, moving_end, moving_start, no_sensor, not_at_rot_target, not_at_target, object_rez, on_rez, remote_data, run_time_permissions, sensor, state_entry, state_exit, timer, touch, touch_start, touch_end, transaction_result, linkset_data, experience_permissions, experience_permissions_denied`

Plus InWorldz extension: `bot_update`

Modern SL events potentially missing from Phlox (test required): `path_update`, `game_control`, `on_damage`, `on_death`, `final_damage`.
