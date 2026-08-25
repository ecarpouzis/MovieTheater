-- Stuntman field anti-blur A/B (2026-08-16). Two statements; run ONE at a time.
--
-- STATUS 2026-08-24: the ARM below is APPLIED IN PROD PERMANENTLY (row 7, guarded single-row UPDATE,
--   verified by read-back; Notes column stamped). This file is now the ROLLBACK record, not a pending
--   experiment. From 2026-08-16 to 2026-08-24 the row sat RESTORED, so every Stuntman room in that window
--   ran the baseline picture -- do not read those sessions as evidence about the filter.
--   Options ship per-room from the site, so it took effect on the next room with no worker recycle.
--   Still open: the +6.6% was measured on a STATIC frame; the motion question needs a driven chase.
--
-- WHAT IS BEING TESTED
--   The game runs its OWN CRTC flicker filter during gameplay: both read circuits point at the
--   SAME framebuffer (FBP=140) one line apart (DBY 0 vs 1), blended 50/50 (MMOD=ALP, ALP=128),
--   with SMODE2 INT=1 FFMD=1. On a 640x224 field buffer that averages every field line with the
--   one below it -- a two-output-row vertical blur baked in before the emulator sees the picture.
--   paraLLEl-GS already suppresses this idiom, but both of its Y branches are gated on
--   alternative_sampling = INT && !FFMD, so it never fires here. lrps2 a57e1e7 adds
--   pcsx2_pgs_antiblur_field to let them fire in field mode.
--
-- PREREQUISITE, AND IT IS NOT OPTIONAL
--   Both GL workers must be running the core built from lrps2 03413e6 or later (11,644,416 B, sha256 ee34c664...). libretro
--   SILENTLY IGNORES an unknown option key, so arming this against the older 2fe1510 core
--   (11,642,368 B, kept as pcsx2_custom_libretro.pre-antiblur.dll) produces a clean run with no effect and no error -- which reads exactly like
--   "the hypothesis was wrong". Verify the DLL first, then arm.
--   As of 2026-08-16 the new core IS deployed on both GL workers.
--
-- HOW TO READ THE RESULT
--   The core logs [crtc-merge] in BOTH arms, always on, gated by a 4-entry ring of recently-seen
--   PMODE/DBY/DY/INT/FFMD signatures plus a 300-frame floor -- about 3 lines per room. Grep it:
--     "FIELD mode, anti-blur NOT armed - the game's blur SURVIVES"  = disarmed arm, filter live
--     "FIELD anti-blur ARMED"                                       = armed arm, filter suppressed
--     "single circuit ... no game-side blur"                        = this frame carries no blend
--   EXPECT ALL THREE IN ONE ROOM. Stuntman ALTERNATES single-circuit and blended on consecutive
--   frames -- a half-strength flicker filter built from two dispenv structs -- so the single-circuit
--   line is not evidence against the lead. That alternation is also why the diagnostic exists at
--   all: a save-state samples the CRTC at one fixed point in the frame loop, which for a per-field
--   register is a systematically PHASE-BIASED sample. Five of six Stuntman states read
--   single-circuit and the live log shows the blend on every other frame.
--
-- MEASURED 2026-08-16 (static "SCENE FAILED" overlay, 4 snaps per arm, V/H = vertical HF over
-- horizontal HF, so horizontal HF controls for scene and encode):
--     disarmed  V/H 0.865 / 0.861 / 0.861 / 0.859
--     armed     V/H 0.928 / 0.915 / 0.915 / 0.915      => approx +6.6% vertical detail
--   STILL OPEN: this is a STATIC frame. Whether suppressing the filter brings 60Hz twitter back in
--   motion is unmeasured and needs eyes on a driven chase.

-- === ARM ===================================================================================
SET QUOTED_IDENTIFIER ON;
UPDATE ArcadeGameProfile
SET CoreOptionsJson = '{"pcsx2_softfloat":"enabled","pcsx2_pgs_field_fullres":"enabled","pcsx2_pgs_ssaa":"16x SSAA (can high-res)","pcsx2_pgs_ss_tex":"enabled","pcsx2_pgs_antiblur_field":"enabled"}'
WHERE Id = 7;
SELECT Id, CoreOptionsJson FROM ArcadeGameProfile WHERE Id = 7;

-- === RESTORE (the shipped 2026-08-08 state; byte-identical to prof7_restore.sql) ============
-- SET QUOTED_IDENTIFIER ON;
-- UPDATE ArcadeGameProfile
-- SET CoreOptionsJson = '{"pcsx2_softfloat":"enabled","pcsx2_pgs_field_fullres":"enabled","pcsx2_pgs_ssaa":"16x SSAA (can high-res)","pcsx2_pgs_ss_tex":"enabled"}'
-- WHERE Id = 7;
-- SELECT Id, CoreOptionsJson FROM ArcadeGameProfile WHERE Id = 7;
