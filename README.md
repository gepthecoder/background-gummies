# Background Gummies

Squishy gummy buddies living on your Windows desktop: they hop on windows, level up, break into apps and play pinball. Unity 6 overlay.

Background-Gummies is a transparent, always-on-top, click-through overlay. The buddies are soft-body gummies that live on your taskbar, your windows and your desktop icons. You can grab them, throw them, feed them, chat with them and play games with them. They grow up, learn tricks, make friends and get up to mischief when you're not looking.

---

## Getting started

1. Open the project in **Unity 6.3** (6000.3.x).
2. Run **WhipIt > Setup Scene + Project Settings** once. Run it again after pulling new features: it adds new components and sounds to the scene.
3. Run **WhipIt > Build And Run (Windows)**. This builds `Builds/WhipIt.exe` and starts it.
4. Quit any time with **Ctrl+Shift+Q**.

You can also press Play in the Editor to try things in the Game view. The real transparent overlay only works in the built app.

---

## Key bindings

### Global hotkeys (work while you're in any app)

| Keys | What it does |
|---|---|
| **Ctrl+Shift+Space** | Open or close the chat box next to the buddy nearest your cursor |
| **Ctrl+C** | Copy something: a snack drops above your cursor (every copy, even with nothing selected) |
| **Ctrl+Shift+L** | Laser pointer on / off: a red dot follows your cursor and the buddies chase it |
| **Ctrl+Shift+K** | Candy cloud: a cotton-candy cloud follows your cursor and rains sweets |
| **Ctrl+Shift+R** | Rain on / off (two minutes of rain, then a rainbow) |
| **Ctrl+Shift+B** | Background mode on / off: buddies live behind your windows, or always in front |
| **Ctrl+Shift+W** | Hide / show the whip |
| **Ctrl+Shift+Q** | Quit WhipIt |

### In the chat box

| Key | What it does |
|---|---|
| **Enter** | Send |
| **Tab** | Talk to the next buddy |
| **Backspace** | Delete |
| **Esc** | Close |

Clicking back into another app also closes the chat.

### Desktop pinball (only while a game is on)

| Key | What it does |
|---|---|
| **Left Shift** | Left flipper |
| **Right Shift** | Right flipper |
| **Space** (hold, let go) | Pull the plunger back and launch. Hold longer to launch harder |
| **Space** (after game over) | Play again |
| **Esc** | Stop the game |

The game takes the keyboard like the chat does, so these keys don't reach your other apps. Click into another app and the game pauses. Click the scoreboard to carry on.

### Mouse

| Action | What it does |
|---|---|
| **Click and drag a buddy** | Pick it up. Let go to drop it, or flick to throw it |
| **Throw a buddy at a window** | It splats, sticks for a moment and peels off. Hard hits knock the window, which springs back |
| **Move the cursor gently over a buddy** | Petting: it gets happy (hearts) |
| **Grab it over and over** | It gets annoyed and hops away from you |
| **Hold a buddy pressed into another buddy** (~0.5 s) | They fuse into a monster |
| **Grab a monster and pull** | The bit you grabbed stretches like taffy; pull hard and a buddy pops out |
| **Hold a buddy pushed deep into a window** (~2 s) | It breaks into the window: the candy window (see below) |
| **Hold the cursor still just above the apple** (level 10+) | It may lasso your cursor and ride along |
| **Click the bare desktop** while time is frozen | Time goes on (slow motion first) |

---

## The buddies

The apple starts as a seed. Everyone else is unlocked by the apple's level.

| Buddy | Arrives | How it grows | Personality and gadget (level 10) |
|---|---|---|---|
| 🍎 **Apple** | From the start | Seed → sprout → apple | The leader. Gets the **whip and the lasso** at level 10 |
| 🍅 **Tomato** | Apple level 5 (planted seed) | Green → orange → ripe red | Competitive. A half-broken **jetpack** (works about 6 times in 10) |
| ☕ **Coffee bean** | Apple level 10 | Green bean → light → medium → dark roast | Jittery and punny. An **espresso shot** (zoomies, then a crash). Steams when you've worked too long without a break |
| 🍔 **Burger** | Apple level 15 (sesame seed) | Slider → burger → big → double burger | Big and laid-back. A **pogo fork**. Its belly is a trampoline |
| 🦖 **Mini dino** | Apple level 20 (an egg hatches) | Grows with its own levels | RAWR. A **super stomp** that knocks snacks off windows |
| 🐥 **Duckling** | Apple level 25 (waddles in) | Duckling → little duck → duck → big duck | Adopts the apple as its mama and follows her everywhere. Loves rain |

Each buddy has its own levels, save file and voice.

### What they learn (by their own level)

| Level | Trick |
|---|---|
| 1 | Hop |
| 2 | Hop down |
| 3 | Hop up low steps (and start learning to dodge your grabs) |
| 4 | Climb walls and window sides |
| 5 | Think two moves ahead |
| 6 | Leap onto windows |
| 7 | Wall jump |
| 8 | Plan whole routes; let a friend have a snack |
| 9 | Catch falling snacks in mid-air |
| 10 | Ace (everything sharper) + **gadget** (apple: whip and lasso) |
| 12 | Swing on a rope (apple) |
| 13 | Skateboard: ollies and kickflips |
| 14 | Paper plane: glide down from high windows |
| 15 | Grind along window tops |
| 16 | Zipline from a high window down to a lower one (everyone can ride it) |
| 17 | Balloon: float up to windows too high to jump to |

They plan a route to every snack and only go for ones they can actually reach.

---

## Things to do

### Everyday life

- **Copy = snack.** Every copy drops a cookie, candy or water drop. They race for it, leap up onto windows and build towers to reach it. About 1 in 25 copies is a **giant potion**.
- **Windows are platforms.** They land on window tops, ride them when you drag them, and fall when you move or minimize them.
- **Desktop icons are perches.** They sit on your icons and hook their ropes on them. The **Recycle Bin** is a trampoline when you throw a buddy onto it.
- **They live in your desktop.** Windows in front hide the buddies standing behind them (Ctrl+Shift+B turns this off).
- **Reactions.** Dizzy after a hard throw, grumpy when stuck, annoyed when poked, happy when petted, asleep (zzz) when nothing happens.
- **Friends.** They visit each other, chat, whisper secrets (and act innocent when your cursor comes close), play tag and nap side by side.
- **Level-up party.** A cake, a candle and confetti when someone levels up; everyone comes over.
- **Back to work!** From level 10 the apple cracks the whip at your cursor after 30 s without input.
- **Take a break.** The coffee bean steams after an hour of work without a break.

### Games and toys

| Thing | How to start | What happens |
|---|---|---|
| **Desktop pinball** | Chat `pinball` | The main screen becomes a pinball table. A buddy is the ball, windows are bumpers (+250), icons are targets (+500, all lit = JACKPOT), the drawn bin between the flippers is the drain. 3 balls, best score saved |
| **Fetch** | Chat `fetch` / `ball` | A tennis ball drops by your cursor. Throw it and they race for it |
| **Basketball** | Chat `basketball` | A hoop clips onto a window corner. Flick the ball at it; they rebound and pass it back. Chat `game` for a pickup game to 5, `dunk`, `shoot`. Three in a row = ON FIRE |
| **Hide and seek** | Chat `hide and seek` | They hide behind windows that reach their floor. Find them by holding the cursor over the spot, moving the window or grabbing what peeks out |
| **Ball pit** | Chat `ball pit` | A padded pit of glossy balls. Chat `cannonball` to make one jump in |
| **Playground slide** | Chat `slide` | A wavy slide from a window corner with a ladder. Chat `go slide` |
| **Buddy tower** | Chat `tower` | 2–3 buddies stack on each other's heads (sometimes TIMBERRR) |
| **Laser pointer** | Ctrl+Shift+L or chat `laser` | They chase the dot like cats |
| **Candy cloud** | Ctrl+Shift+K or chat `candy cloud` | It follows your cursor raining sweets; they eat them |
| **Giant potion** | Chat `potion` | Whoever drinks it grows huge for 15 s, stomps around and gives the others a ride. Chat `shrink` to end it |
| **Trophy shelf** | Chat `trophies` | Bronze / silver / gold medals for lava wins, dunks, buckets, hide and seek, towers and jams |
| **Desktop band** | Chat `jam` | Every window top is a note (wider = lower). Landing on a window plays it. Jams also happen by themselves, and they learn the tune you play by throwing them |

### The world

| Thing | How to start | What happens |
|---|---|---|
| **Rain** | Ctrl+Shift+R or chat `rain` | Rain clouds roll in for two minutes. The duck loves it, the rest hide. Puddles form (chat `puddle` to splash), then a rainbow |
| **The floor is lava** | Chat `lava` | Lava rises along every floor. Rescue stranded buddies by dragging a window under them or holding them. Last ones standing win. Also happens by itself while you're away |
| **Bullet time** | Chat `freeze` | Time stops dead. Move things around, then click the bare desktop or chat `go`: slow motion back to normal |
| **Day & night** | Automatic (your clock) | Sleepy from 21:00, a pillow fort at about 21:40, asleep in a pile with dreams about the day, up again after 06:00 |
| **Caught in the act** | Automatic after 2 min away (or chat `go wild`) | They party while you're gone: trampoline on the Recycle Bin, pillow fights, conga, dancing. Touch the mouse and they freeze, then act innocent |

### Fused monsters

Hold one buddy pressed into another for about half a second and they squash into one monster. Every combination is its own monster with its own name (there are 57, from APPLOSAURUS to GUMMAGEDDON). Monsters lumber, roar, eat snacks in one chomp and chase the others, who run and hide. Pull a buddy out to split them. Monsters stay fused after a restart until you pull them apart.

### The candy window (window infestation)

Hold a buddy and push it deep into a window (3.2 × 2.4 units or bigger, never a maximized one) for about two seconds. The window shudders, jelly seeps in, then SPLORCH: the window turns into a candy panel in the buddy's colour, with the buddy living inside in Candy Land.

- **Top bar:** drag it to move the real window. **OUT** lets the buddy out.
- **Tabs:**
  - **ME**: its card (level, XP, medals, best friend, windows visited).
  - **PLAY / TOYS / WORLD / GROW**: buttons for everything in this README, live.
- **Getting out:** drag the buddy past the window's edge, press OUT, chat `come out`, or close the window.
- **Taking over:** push a second buddy in and it knocks the first one out and takes over.

Afterwards it tells the others what it saw. Nobody believes it, unless they've been inside too.

### Several screens

The overlay spans all your screens. Every screen has its own floor, buddies wander between screens and you can throw them across. A screen driven by a different graphics chip than the one drawing WhipIt (for example a laptop's own screen on integrated graphics) is left out, because Windows can't show a see-through window correctly across two chips.

---

## Chat commands

Open the chat with **Ctrl+Shift+Space**. Replies are scripted, each buddy in its own voice. Words are matched whole, so `hi` doesn't match `this`.

**Moves and gadgets:** `jump` / `hop` / `dance` · `come here` · `sleep` / `nap` · `tag` / `catch me` · `snack` · `swing` · `lasso me` / `carry me` · `zipline` · `skate` / `kickflip` · `grind` · `fly` / `paper plane` · `balloon` · `whip` · `jetpack` · `espresso` · `pogo` · `stomp` · `rawr` · `dodge me` / `stop dodging`

**Games and toys:** `pinball` / `stop pinball` · `fetch` · `basketball` / `game` / `dunk` / `shoot` / `no hoop` · `hide and seek` · `ball pit` / `cannonball` / `no ball pit` · `slide` / `go slide` / `no slide` · `tower` · `laser` / `laser off` · `candy cloud` · `potion` / `shrink` · `trophies` · `jam` / `quiet` / `music on`

**The world:** `rain` / `stop the rain` / `puddle` · `lava` / `no lava` · `freeze` / `go` · `bedtime` / `wake up` · `go wild` · `background` / `on top` · `come out` (from a candy window)

**About you** (kept in `chat_memory.json` on this PC only):
- **Tell it things:** `my name is …`, `i like …`, `i'm working on …`, `remember that …`
- **Ask what it knows:** `what's my name`, `what do i like`, `what do you remember`
- **Forget:** `forget my name`, `forget everything`

**Your day and its stats:** `how long have i worked` · `last break` · `my day` · `how many times did i copy` · `what time is it` · `what day is it` · `level` · `what can you do` · `what's next` · `how many snacks`

**Diagnostics:** `test colours` (a colour test card on every screen) · `debug faces`

---

## Editor menu (for development)

| Menu | What it does |
|---|---|
| **WhipIt > Setup Scene + Project Settings** | Builds or updates the scene and settings |
| **WhipIt > Build And Run (Windows)** | Builds `Builds/WhipIt.exe` and runs it |
| **WhipIt > Buddy > …** | Jump to any level or unlock (reset to seed, skip to apple, one snack before each friend, plant / hatch now) and start any feature in Play mode (party, tower, hoop, rain, lava, bedtime, jam, candy cloud, ball pit, slide, pinball, freeze time, go wild, fuse / split, giant potion, trophies…) |
| **WhipIt > Debug > Dump Faces** | Writes `whipit_faces.txt` |

In the Editor you can test with three fake desktop icons and a fake window set up by Setup.

---

## Files

Saves and logs live in `%USERPROFILE%\AppData\LocalLow\DefaultCompany\WhipIt`:

| File | What's in it |
|---|---|
| `buddy_*.json` | Each buddy's level, XP and progress |
| `chat_memory.json` | What you told them in the chat |
| `today.json` | Today's work time and breaks |
| `dreams.txt` | Today's moments (for their dreams) |
| `monsters.txt` | Who's fused right now |
| `windows.txt` | Candy-window visits and best friends |
| `trophies.txt` | Medals |
| `pinball.txt` | Best pinball score |
| `pending_level.txt` | A level change waiting for the next start |
| `whipit_screens.txt` | What it found about your screens |
| `whipit_perf.txt` | Speed report every 10 s (frame times, where the time goes, desktop icons) |

---

## Privacy

WhipIt keeps everything on your PC. It only reads what it needs:

- **Idle time:** how long since you last touched the mouse or keyboard, never what you pressed.
- **Copying:** the clipboard's change counter and whether Ctrl+C is held. Never the clipboard's content.
- **Keys:**
  - The hotkey chords listed above.
  - Shift and Space only while a pinball game is on and WhipIt has the keyboard.
  - In the chat box, only what you type to it.
- **Windows:** their positions and sizes. Never their contents.
- **Desktop icons:** their positions only. Never their names or files, and they're never moved.
- **Your cursor:** read, never moved. Windows are only nudged and always spring back exactly; maximized windows are never touched.
