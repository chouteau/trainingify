# Design QA — Workout ERG toggle

- Source visual truth path: `C:\Users\marc_\AppData\Local\Temp\codex-clipboard-48eede34-50a3-46d0-a217-0fb063bc8894.png`
- Implementation screenshot path: `C:\Projects\Trainingify\trainingify-workouts-erg.png`
- Disabled-state screenshot path: `C:\Projects\Trainingify\trainingify-workouts-erg-off.png`
- Combined comparison path: `C:\Projects\Trainingify\trainingify-erg-comparison.png`
- Viewport: 1287 × 740 native Windows application window
- State: active workout selected; ERG enabled and disabled states checked

**Full-view comparison evidence**

The original six metric cards retain their dark background, border, radius, spacing, uppercase labels, value hierarchy, and accent colors. The new ERG card extends the same row without changing the card height or surrounding container rhythm.

**Focused region comparison evidence**

The combined comparison shows the original metric strip above the implemented strip. The added switch uses the existing application switch component: green with `ACTIVÉ` when on, neutral gray with `DÉSACTIVÉ` when off. Both states remain centered and readable at the captured desktop viewport.

**Findings**

- No actionable P0/P1/P2 visual mismatch.
- Fonts and typography: existing app font, uppercase labels, weight, size, and spacing are preserved.
- Spacing and layout rhythm: seven equal columns fit without clipping at the target viewport; responsive fallbacks reduce to four and two columns.
- Colors and visual tokens: existing card, border, muted-text, and green success tokens are reused.
- Image quality and asset fidelity: no new raster or icon asset is required for this native form control.
- Copy and content: `MODE ERG`, `ACTIVÉ`, and `DÉSACTIVÉ` clearly expose both states in French.

**Comparison history**

- Initial implementation: no P0/P1/P2 finding; no visual correction loop required.
- Interaction check: switch changed from enabled to disabled and the label/color updated correctly.

**Implementation Checklist**

- [x] Match the existing metric-card visual language.
- [x] Verify enabled and disabled interaction states.
- [x] Preserve desktop layout and add responsive breakpoints.

**Follow-up Polish**

- None required for the requested scope.

final result: passed
