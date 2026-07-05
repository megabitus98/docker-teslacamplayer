# Product

## Register

product

## Users

Self-hosting Tesla owners running TeslaCamPlayer (usually in Docker on a home server/NAS) to browse, review, and export their TeslaCam dashcam and Sentry Mode footage. Technical enough to set env vars, but in a hurry when reviewing an incident.

## Product Purpose

A fast web viewer for TeslaCam archives: browse events on a timeline, play all camera angles in sync, inspect telemetry, export clips. Success = finding and reviewing the right clip in seconds, even in 250k+ file libraries.

## Brand Personality

Utilitarian, calm, unobtrusive. The footage is the star; the UI disappears into the task.

## Anti-references

- Flashy SaaS dashboards (gradients, hero metrics, decorative motion)
- Over-dense config UIs that dump raw env-var plumbing at the user

## Design Principles

- The tool disappears into the task — standard MudBlazor affordances, no invented controls
- Dark and light both first-class (theme follows system preference)
- Density where data lives (event list, telemetry), breathing room in forms and dialogs
- Progressive disclosure: advanced/diagnostic detail is available but never in the way

## Accessibility & Inclusion

No formal WCAG target. Respect system dark-mode preference, keep MudBlazor's keyboard/focus behavior intact, maintain readable contrast in both themes.
