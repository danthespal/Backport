*PS5 BACKPORT — TESTER CHECKLIST*

Thank you for testing. Please follow every step and send the completed report, even if the game does not start. A failed test with logs is valuable.

*BEFORE YOU START*

• Use the *exact game title ID and version* named in the release post.
• You need a jailbroken PS5 with *ShadowMountPlus* and kstuff-lite. Do *not* run the standalone BackPork payload too — it conflicts with ShadowMountPlus.
• Turn ShadowMountPlus debug logging on. It is normally already on. Only change `/data/shadowmount/config.ini` if it contains `debug=0`; set it to `debug=1`.
• You need FTP access to copy the game and collect logs.

*INSTALL*

1. On your PC, open the decrypted game dump for the exact title and version.

2. Copy the *contents* of the backport release folder over the game folder. Choose *overwrite/replace* when asked.

3. Handle `ampr_emu.index` after merging:

   • *The release includes it:* keep it. It belongs to this exact game dump and this exact backport build, and replaces any old index from the dump.

   • *The release does not include it:* delete any `ampr_emu.index`, `ampr_emu.index.tmp`, and `ampr_emu.index.bak` from the merged game folder. AMPR will make a fresh index on the first launch.

   Never copy an index from another game version or another backport build.

4. Copy the complete merged game folder to a ShadowMountPlus scan folder, for example:
   `/data/homebrew/<GAME>/`

5. In `/data/shadowmount/config.ini`, leave `auto_update_ampr=0` (the default). This makes the game use the AMPR library supplied with the backport.

6. Start the game. If it does not start or shows an error, stop and collect the logs below before trying anything else.

*TEST*

Please test as much as you can:

1. *Boot* — reaches the main menu.
2. *New save* — create a save or change and save a setting; no error appears.
3. *Save and reload* — make a second save, return to the system menu, restart, then load it.
4. *Sound: TV* — music and effects play.
5. *Sound: headphones* — controller jack or headset audio works.
6. *Play for 10+ minutes* — menus, loading, and normal offline play; no freeze or crash.
7. *Rest mode (optional)* — enter rest mode while in-game, wake the console, and continue playing.

Tests 2–5 are especially important.

If the game crashes only after playing for a while, add this line to `/data/shadowmount/config.ini`, restart ShadowMountPlus, test once more, and report the result:

```
kstuff_no_pause=<TITLE_ID>
```

*COLLECT THESE FILES*

Copy them immediately after testing, before launching anything else:

• `/data/shadowmount/debug.log`
• `/data/shadowmount/debug.log.1` — send this too if the console crashed/rebooted and you jailbroke again.
• `ampr_emu.log`, if it exists in the game folder.
• The on-screen crash code, for example `CE-xxxxxx-x`; a photo or video is welcome.

*ARCHIVE AND SEND*

Put the completed report, all logs, photos/videos, and the exact backport archive (or its checksum) into one archive. Send it to the dedicated Telegram channel for *this game*. Do not send it to another game's channel.

*REPORT — COPY, FILL IN, AND SEND*

```
Game / title ID / version:
Backport folder name:
Console: PS5 / Slim / Pro — disc or digital:
Console firmware:
Jailbreak / kstuff-lite / ShadowMountPlus versions:
AMPR index: supplied with release / rebuilt on console / no index found
Game folder copied to: /data/homebrew/

1. Boot:               pass / fail / not tested
2. New save:           pass / fail / not tested
3. Save + reload:      pass / fail / not tested
4. Sound, TV:          pass / fail / not tested
5. Sound, headphones:  pass / fail / not tested
6. 10+ min play:       pass / fail / not tested   (minutes played: )
7. Rest mode:          pass / fail / not tested

Crash code (if any):
What happened:
kstuff_no_pause retry: not needed / helped / didn't help
Files attached: debug.log, debug.log.1, ampr_emu.log, photo/video
Archive sent to this game's Telegram channel: yes / no
```
