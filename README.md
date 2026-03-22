# Marketplace Fixer Enhanced V2

Marketplace Fixer Enhanced V2 is a major overhaul of the original Marketplace Fixer for BeamNG marketplace config editing.

This version was built as one large upgrade delivered in chunks, with the goal of turning the tool from a simple config fixer into a smarter, safer, review-driven workspace for scanning mods, autofilling data, handling internet-assisted lookup, reviewing uncertain matches, and saving changes more reliably.

## What V2 changes

V2 focuses on five major areas:

- workflow and autofill architecture
- responsiveness and save performance
- safer write and save integrity
- smarter scanning, inference, and lookup
- stronger review, diagnostics, and persistence

## Main improvements in V2

### Smarter workflow core
- added a workflow/service layer to centralize autofill, retry, and staged inference behavior
- bulk autofill and review/retry now share the same core pipeline
- single-config autofill no longer runs the heaviest inference path directly on the UI thread
- large runs defer expensive workspace refreshes until the end instead of constantly rebuilding state mid-run

### Better bulk responsiveness
- reduced per-item UI churn during Auto Fill All
- throttled progress and status updates so long runs feel less frozen and less noisy
- moved grouped batch writes off the main window thread
- retrying reviewed items now uses the same more responsive workflow behavior
- manual save responsiveness was improved with follow-up save hotfixes so saving no longer freezes the app as badly

### Safer save and write handling
- added atomic file replacement helpers for config writes and zip rewrites
- zip rewrites now use safer temp-write/replace flow
- config saves now stage data before committing live item state
- Auto Fill All and Bulk Edit now use staged working copies so failed writes do not leave false in-memory success state
- added shared write gating to reduce overlapping save/rewrite problems
- save behavior was further refined so workspace state is only committed after successful disk writes

### Mod-first scanning and better mod handling
- introduced mod-level scan records
- added support for Vehicle, Map, Mixed, and Unknown mod classification
- added map-capable mod detection
- map-only mods can now appear in the workspace instead of being lost behind vehicle-only assumptions
- dashboard and summaries now reflect truer mod-level scan data

### Better inference and internet lookup
- added explicit confidence score and confidence tier handling
- added identity evidence and valuation evidence tracking
- strengthened review-hold behavior so weaker/conflicting matches are less likely to apply silently
- improved conflict handling between local mod evidence and online suggestions
- improved manual lookup result ordering for make/model/year/body-style relevance
- improved lookup timeout handling so manual lookup is more likely to return useful partial or strong results
- manual lookup now returns more canonical matched vehicle identity data instead of just echoing raw search text

### Better diagnostics and trust
- Internet Lookup now shows clearer live status, timeout/no-result feedback, confidence badges, and match summaries
- Configuration Editor now includes a diagnostics card for confidence, review state, identity evidence, and valuation evidence
- Review Queue now exposes confidence more clearly
- manual lookup updates the selected config’s confidence and evidence immediately
- the tool now does a better job explaining why something was autofilled, held for review, or updated from lookup

### Stronger persistence and workspace durability
- settings, crash logs, review state, mod memory, and pricing cache now share one app-data root
- app-state writes now use safer atomic persistence with backup fallback
- persisted config memory now retains newer diagnostic/history fields like confidence, evidence, review reason, decision origin, and lookup trail
- review and lookup history survive relaunches more reliably
- the workspace now behaves more like a durable long-term tool instead of a disposable one-pass scanner

### Cleaner internal architecture
- centralized config parsing and hydration into shared services
- reduced duplicated scanner/editor parsing logic
- scanner and editor flows now hydrate config data through more consistent code paths
- future fixes should be safer because more of the project now shares the same internal logic

### Smarter review and retry behavior
- added review scoring and classification so weak matches are not all treated the same
- review reasons are now grouped into clearer categories such as identity conflict, missing identity, year conflict, metadata conflict, weak evidence, and value uncertainty
- review queue ordering now prioritizes the worst conflicts first
- retry ordering now better prioritizes reviewed and hinted items
- later V2 fix and stabilization passes tightened review truth so value/population-only uncertainty is less likely to behave like full identity failure when identity is otherwise usable
- ignored review counts and grouped review behavior were cleaned up so the queue reflects review truth more accurately

## What V2 is meant to be

Marketplace Fixer Enhanced V2 is no longer just a one-pass marketplace config fixer.

It is meant to be:
- a smarter mod and config review workspace
- a safer editor with better write integrity
- a more explainable autofill tool
- a more persistent long-term utility that remembers confidence, evidence, and review history
- a stronger base for future marketplace editing, lookup, and review features

## Build

Open the project in Visual Studio and build in Release mode.

Typical local commands:

```powershell
dotnet restore .\WpfApp1\marketplace.fixer.csproj
dotnet build .\WpfApp1\marketplace.fixer.csproj -c Release
dotnet run --project .\WpfApp1\marketplace.fixer.csproj