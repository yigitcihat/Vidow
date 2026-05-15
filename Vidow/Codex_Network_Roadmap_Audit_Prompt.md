# Codex / kod asistanı için prompt (kopyala-yapıştır)

Aşağıdaki İngilizce metni olduğu gibi (gerekirse `Repository root` satırını doldurarak) Codex veya başka bir asistana verin. Türkçe çıktı istiyorsanız **PROMPT SONU**’ndan hemen önce verilen cümleyi ekleyin.

---

## PROMPT BAŞLANGICI

You are a senior Unity multiplayer engineer auditing a production codebase. The game uses **Photon Fusion** in **`GameMode.Shared`** (client-hosted / master client authority): one player’s device effectively runs authoritative simulation for the session while **Photon Cloud** carries connectivity and replication. The project also uses **host migration** (`OnHostMigration`, `IsSharedModeMasterClient`), **NetworkOrchestrator** / connection state machine, **Firebase** and **Unity Authentication / Friends** for invites and ancillary services.

**Your goals (in order):**

1. **Map the online gameplay pipeline** from matchmaking / session create-join through scene loads (`LevelStart`, `Level1`, etc.), spawn, in-race state, mini-games, and disconnect/end-game—so you understand how host authority affects each phase.
2. **Identify failure modes** that degrade UX: wrong or missing user feedback on **network errors**, **timeouts**, **host left**, **migration**, **reconnect**; **soft-locks** (stuck on a screen, black screen, infinite wait); **desync** or **orphan UI** after shutdown; **race conditions** between async scene unload and Fusion callbacks (e.g. session properties, ES3).
3. **Propose concrete fixes** per issue: file-level pointers, not vague advice. Prefer minimal, testable changes; do not rewrite unrelated systems.
4. **Host quality (still Shared mode, no paid dedicated server assumption):** recommend **measurable** improvements: replication / tick / payload hot spots, when to reduce update frequency, dangerous `[Networked]` fields, RPC spam, large state sync; **master selection** if the codebase allows; tuning **timeouts** and **focus-loss / migration** behavior so migration is rare and predictable; alignment with `Assets/Photon/Fusion/Resources/NetworkProjectConfig.fusion` (e.g. tick, connection timeouts, host migration settings).
5. **User messaging:** define a **consistent policy**: when to show **retry**, **return to menu**, **reconnecting…**, **host migrated** (silent vs explicit), localized strings if present; avoid lying to the user (e.g. “connecting” forever). Audit **UiManager** flags like “show host left when LevelStart loads” and similar.

**Code areas to search and read systematically (non-exhaustive):**

- `Assets/_Project_/Scripts/Networking/Fusion/FusionManager.cs` — `StartGame`, `CreateSession`, `JoinSession`, shutdown, focus loss, host migration triggers, session visibility/open flags.
- `Assets/_Project_/Scripts/Core/Services/Network/NetworkOrchestrator.cs` and `ConnectionStateMachine.cs` (under `Assets/_Project_/Scripts/Core/Services/Network/`).
- `Assets/_Project_/Scripts/Core/Managers/GameManager.cs` and related partials (lifecycle, callbacks, reconnect, offline vs online).
- Matchmaking / UI: `Assets/_Project_/Scripts/UI/Panels/MatchMaking/PlayerMatchmakingSlotHandler.cs`, friends flow, `LeaveSession`, loaders.
- Analytics / logging hooks for network errors (e.g. `TechAnalytics.TrackNetworkError`) to correlate with UX.

**Deliverables (structure your response exactly like this):**

1. **Executive summary** (5–10 lines): current topology and top 3 UX risks.
2. **Gameplay × network flow diagram** (mermaid): states from “searching match” to “in game” to “disconnect/end”, noting where **host authority** matters.
3. **Issue register** (table): ID | Symptom | Likely cause | Severity (P0–P3) | Evidence (file:symbol or line range) | Proposed fix | Test plan.
4. **Roadmap**: Phase 0 (quick wins, &lt;1 week) | Phase 1 (stability) | Phase 2 (host-quality / netcode hygiene) | Phase 3 (optional larger refactors). Each item must be **actionable** and tied to issue IDs.
5. **User-facing copy guidelines**: short list of **canonical messages** and when each is shown; list screens that must **always** exit to a safe state on failure.
6. **Explicit non-goals** for this audit: e.g. migrating to dedicated Fusion Server or changing Photon pricing tier—only mention if clearly justified as a future spike.

**Constraints:**

- Do not assume access to Photon dashboard; infer from code and config only.
- If something is uncertain, label it **Hypothesis** and say what log or metric would confirm it.
- Respect existing patterns (ServiceLocator, ES3, Unity UI) unless a change is clearly justified.

Repository root: `[PROJE_KÖKÜ veya current workspace]`.

(Optional — Turkish output) Add this line at the end of your message to the assistant: `Write the deliverables in Turkish; keep file paths and code identifiers in English.`

## PROMPT SONU

---

## Kullanım notları

- Tek seferde tüm repoyu göremeyebilir; gerekirse aynı promptu **bölüm bölüm** tekrarlayın (ör. önce `FusionManager` + `NetworkOrchestrator`, sonra `GameManager` + reconnect).
- Türkçe teslimat için prompt içindeki opsiyonel cümleyi **Repository root** satırının altına eklemeniz yeterli.
