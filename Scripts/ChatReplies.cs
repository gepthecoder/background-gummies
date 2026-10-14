using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

/// <summary>
/// The buddies' scripted chat answers (until the AI agent takes over via BuddyChat.ReplyOverride): keyword rules with
/// a bit of personality per buddy, some of which make it do something (the action runs when it answers).
/// They remember what you tell them about you (ChatMemory: your name, likes, favourites, what you're working on,
/// "remember that..."), greet you for the time of day, and answer questions about themselves and your day (WorkClock).
/// </summary>
public static class ChatReplies
{
    /// <summary>Answer to what you typed. action: what it does as it answers; close: the chat box closes after it.</summary>
    public static string Reply(GummyBody b, string message, BuddyChat chat, out Action action, out bool close)
    {
        action = null;
        close = false;
        var speech = b.GetComponent<BuddySpeech>();
        var brain = b.GetComponent<GummyBrain>();
        var progress = b.GetComponent<BuddyProgress>();
        var whip = b.GetComponent<BuddyWhip>();
        var v = speech ? speech.voice : BuddySpeech.Voice.Apple;
        bool tomato = v == BuddySpeech.Voice.Tomato, bean = v == BuddySpeech.Voice.Bean;
        bool hasFriend = GummyBody.All.Count > 1;
        string m = " " + Normalize(message) + " ";
        bool question = message.TrimEnd().EndsWith("?");
        // bullet time (BulletTime): "freeze" stops time, "go" lets it run again (slow motion first). First, so it works for
        // everyone, asleep or fused
        if (BulletTime.Current)
        {
            bool monster = v == BuddySpeech.Voice.Monster, duck = v == BuddySpeech.Voice.Duck;
            if (BulletTime.Stopped && Has(m, GoWords))
            {
                close = true;
                Vector2 at = b.Center;
                action = () => { if (BulletTime.Current) BulletTime.Current.Go(at); };
                return monster ? "WE... MOVE AGAIN" : duck ? "QUACK! GO!" : V(v, "AND... ACTION!", "GO GO GO!", "UN-PAUSE!", "AND... BUN-PAUSE!", "RAWR! GO!");
            }
            if (Has(m, FreezeWords))
            {
                if (BulletTime.Stopped) return monster ? "WE ARE STONE" : duck ? "QUACK. (FROZEN)" : V(v, "WE ARE! SAY GO", "ALREADY FROZEN!", "CAN'T... MOVE...", "FROZEN BUN!", "STATUE MODE");
                close = true;
                action = () => { if (BulletTime.Current) BulletTime.Current.Freeze(b); };
                return monster ? "TIME... STOPS" : duck ? "QUACK! FREEZE!" : V(v, "FREEZE!", "TIME... STOP!", "HOLD IT RIGHT THERE!", "SAY BUN-CHEESE!", "RAWR! STOP!");
            }
        }
        // a colour test card on every screen (ColorTest): what Windows really shows for exact colours
        if (Has(m, "test colors", "test colours", "test color", "test colour", "color test", "colour test"))
        {
            close = true;
            action = () => ColorTest.Show(b.spriteMaterial);
            return "TEST CARDS! TAKE A SCREENSHOT";
        }
        // desktop pinball (PinballTable): the main screen turns into a pinball table, a buddy is the ball
        if (PinballTable.Current && Has(m, "stop pinball", "no pinball", "no more pinball", "end pinball", "quit pinball", "pinball off", "stop the pinball", "enough pinball"))
        {
            if (!PinballTable.Playing) return Pick("WHAT PINBALL?", "NO GAME ON");
            close = true;
            action = () => { if (PinballTable.Current) PinballTable.Current.Close(); };
            return v == BuddySpeech.Voice.Duck ? "QUACK. GOOD GAME" : V(v, "GOOD GAME!", "AWW. GG", "BUN-BYE TABLE", "GG! NEXT TIME", "RAWR. GG");
        }
        if (PinballTable.Current && Has(m, "pinball", "pin ball", "play pinball", "desktop pinball", "lets play pinball"))
        {
            if (PinballTable.Playing) return Pick("WE'RE PLAYING! SHIFT KEYS!", "FLIP! FLIP! SHIFT KEYS!");
            close = true;
            action = () => { if (PinballTable.Current && !PinballTable.Current.Begin() && speech) speech.Say(Pick("NOBODY TO PLAY...", "NO TABLE HERE..."), 0f, true); };
            return v == BuddySpeech.Voice.Duck ? "QUACK! PINBALL! SHIFT KEYS FLIP!" : V(v, "PINBALL! SHIFT FLIPS, SPACE LAUNCHES!", "PINBALL! I CALL BALL!", "PINBALL! SHIFT KEYS FLIP!", "PINBALL! HOLD SPACE, LET GO!", "RAWR! PINBALL!");
        }
        // inside a window it broke into (WindowInfestation): "come out" and out it hops
        if (b.InRoom && WindowInfestation.Current && Has(m, "come out", "get out", "out you go", "come back", "leave the window", "out of the window", "come out of there"))
        {
            close = true;
            action = () => { if (WindowInfestation.Current) WindowInfestation.Current.ComeOut(b); };
            return v == BuddySpeech.Voice.Duck ? "QUACK... OK" : V(v, "AWW, OK...", "FIVE MORE MINUTES?", "BUT IT'S SO NICE IN HERE", "FINE. OUT.", "RAWR... OK");
        }
        // bedtime (Bedtime): asleep, it talks in its sleep; "wake up" wakes it (grumpy), or everyone when it's morning
        if (Bedtime.Sleeping && brain && brain.Current == GummyBrain.State.Asleep)
        {
            if (Has(m, "wake up", "good morning", "morning", "rise and shine", "wakey wakey", "get up", "wake up everyone"))
            {
                close = true;
                if (Bedtime.Instance.CanWakeAll) { action = () => { if (Bedtime.Instance) Bedtime.Instance.Morning(); }; return v == BuddySpeech.Voice.Duck ? "*YAWN* QUACK!" : V(v, "*YAWN* MORNING!", "*YAWN* ALREADY?", "*YAWN* COFFEE!", "*YAWN* BUN-JOUR", "*YAWN* RAWR"); }
                action = () => { if (brain) { brain.WakeUp(); brain.Grumpy(5f); } };
                return v == BuddySpeech.Voice.Duck ? "QUACK... IT'S NIGHT" : V(v, "IT'S NIGHT! 5 MORE MINUTES...", "NOOO. SLEEPING.", "DECAF... ZZZ...", "BUN... TIRED...", "RAWR... IT'S DARK");
            }
            return Bedtime.Instance.SleepTalk(v);
        }
        if (v == BuddySpeech.Voice.Monster) // (a fused monster: it only talks like one; it does play the tuba)
        {
            if (Has(m, JamWords))
            {
                if (BandJam.Current) return "SHH. WE ARE PLAYING \u266A";
                close = true;
                action = () => BandJam.Begin(b, true);
                return Pick("WE PLAY THE TUBA. RAWR", "MUSIC SOOTHES US \u266A");
            }
            return MonsterReply(m, speech);
        }

        // what you tell it about you (first: "i like snacks" isn't asking for one)
        string r = AboutYou(m, message, v, question);
        if (r != null) return r;
        // your day, the time, and its own stats
        r = Stats(m, b, v, progress, brain);
        if (r != null) return r;

        // things to do
        // bedtime now (Bedtime): the pillow fort goes up
        if (Bedtime.Instance && Has(m, "pillow fort", "bedtime", "bed time", "time for bed", "go to bed", "lights out", "blanket fort", "fort"))
        {
            if (Bedtime.Sleeping) return Pick("WE'RE IN BED!", "ZZZ...");
            close = true;
            action = () => { if (Bedtime.Instance) Bedtime.Instance.Night(); };
            return v == BuddySpeech.Voice.Duck ? "QUACK! PILLOWS!" : V(v, "PILLOW FORT!!", "FORT TIME!", "DECAF MODE...", "PILLOWS, PLEASE!", "RAWR! COZY!");
        }
        // the band (BuddyBand, BandJam): quiet, music on, a jam
        if (Has(m, "quiet", "be quiet", "shh", "shhh", "shush", "no music", "music off", "stop the music", "stop music", "mute", "silence", "stop playing"))
        {
            if (BuddyBand.Muted && !BandJam.Current) return Pick("WE ARE QUIET!", "SHHH!");
            action = () => BuddyBand.Muted = true;
            return v == BuddySpeech.Voice.Duck ? "QUACK... (QUIETLY)" : V(v, "OK... *WHISPERS*", "FINE. NO MORE NOTES", "SILENT BEAN", "BUN ON MUTE", "QUIET RAWR...");
        }
        if (Has(m, "music on", "unmute", "sound on", "band on", "notes on"))
        {
            action = () => BuddyBand.Muted = false;
            return v == BuddySpeech.Voice.Duck ? "QUACK \u266A" : V(v, "YAY! \u266A", "BACK ON!", "BEANS GOT RHYTHM!", "BUN-TASTIC!", "RAWR \u266A");
        }
        if (Has(m, JamWords))
        {
            if (BandJam.Current) return Pick("WE'RE PLAYING! \u266A", "LISTEN! \u266A");
            close = true;
            action = () => BandJam.Begin(b, true);
            return v == BuddySpeech.Voice.Duck ? "QUACK! JAM TIME!" : V(v, "BAND TIME! \u266A", "LET'S ROCK!", "JAM-PRESSO!", "BEATBOX TIME!", "RAWR-OCK!");
        }
        if (Has(m, "hide and seek", "hide n seek", "hideandseek", "hide", "seek", "hiding"))
        {
            action = () => BuddyHideSeek.StartGame(b.GetComponent<BuddyHideSeek>());
            close = true;
            return tomato ? Pick("YESSS!", "YOU'LL NEVER FIND ME") : bean ? Pick("STEALTH MODE!", "BEAN GONE!") : Pick("OOH OK!", "YES!!");
        }
        if (Has(m, "lasso", "lasso me", "rope", "carry me", "ride", "spiderman", "spider man", "spidey", "web"))
        {
            var rope = b.GetComponent<BuddyRope>();
            var sk = b.GetComponent<BuddySkills>();
            if (sk && !sk.RopeKit) return Pick("ONLY APPLE HAS A ROPE", "NO ROPE. I'VE GOT A " + sk.GadgetName + "!");
            if (!rope || (sk && !sk.Has(BuddySkills.Skill.Lasso))) return sk ? $"AT LEVEL {sk.LevelFor(BuddySkills.Skill.Lasso)}!" : Pick("NO ROPE :(", "SOMEDAY!");
            close = true;
            if (rope.CanLasso(out _)) { action = () => rope.LassoCursor(); return V(v, "GIDDY UP!", "HOLD ON!", "ROPE TIME!"); }
            rope.LassoSoon(10f); // as soon as your cursor's above it
            return V(v, "PUT YOUR CURSOR ABOVE ME!", "CURSOR ABOVE ME, GO!", "HOLD IT ABOVE ME!");
        }
        if (Has(m, "jetpack", "jet pack", "jet", "rocket", "blast off", "lift off"))
        {
            var sk = b.GetComponent<BuddySkills>();
            if (sk && sk.Gear != BuddySkills.Kit.Jetpack) return V(v, "NO JETPACK. TOMATO HAS ONE", "", "ASK TOMATO. IF IT WORKS", "ME? FLY? NO", "I CAN'T FLY. YET");
            if (sk && !sk.Has(BuddySkills.Skill.Gadget)) return $"AT LEVEL {sk.LevelFor(BuddySkills.Skill.Gadget)}!";
            if (!b.GetComponent<BuddyJetpack>()) return Pick("NO JETPACK :(", "WHAT JETPACK?");
            if (brain && brain.Current != GummyBrain.State.Idle) return Pick("HOLD ON!", "IN A SEC!");
            if (brain && brain.JetNow()) { close = true; return Pick("STAND BACK!", "3... 2... 1...", "PLEASE WORK, PLEASE WORK"); }
            return b.OnPlatform ? Pick("NOTHING HIGHER HERE", "UP WHERE?") : Pick("NOTHING TO FLY UP TO", "NEED A WINDOW UP THERE");
        }
        if (Has(m, "espresso", "shot", "espresso shot", "caffeine", "drink coffee", "zoomies"))
        {
            var es = b.GetComponent<BuddyEspresso>();
            var sk = b.GetComponent<BuddySkills>();
            if (!es || (sk && sk.Gear != BuddySkills.Kit.Espresso)) return V(v, "COFFEE IS BEAN'S THING", "NO THANKS. I'M JITTERY ENOUGH", "", "MAYBE DECAF", "DINOS DON'T DRINK COFFEE");
            if (!es.Has) return sk ? $"AT LEVEL {sk.LevelFor(BuddySkills.Skill.Gadget)}!" : "SOMEDAY!";
            if (es.Buzzing || es.Crashing) return Pick("ALREADY BUZZING!!", "NO MORE. MY HANDS...");
            if (es.Drink(true, true)) { close = true; return Pick("ONE SHOT, COMING UP!", "DOUBLE SHOT!", "BEAN FUEL!"); }
            return Pick("IN A SEC!", "LET ME CALM DOWN FIRST");
        }
        if (Has(m, "pogo", "pogo fork", "pogo stick", "fork"))
        {
            var pg = b.GetComponent<BuddyPogo>();
            var sk = b.GetComponent<BuddySkills>();
            if (!pg || (sk && sk.Gear != BuddySkills.Kit.Pogo)) return V(v, "THE FORK IS BURGER'S", "BOUNCE ON BURGER!", "BOING-PRESSO? NO", "", "I STOMP, NOT BOUNCE");
            if (!pg.Has) return sk ? $"AT LEVEL {sk.LevelFor(BuddySkills.Skill.Gadget)}!" : "SOMEDAY!";
            if (pg.Bounce()) { close = true; return Pick("BOING TIME!", "HOLD MY BUN", "WHEEE!"); }
            return Pick("IN A SEC!", "NOT HERE");
        }
        if (Has(m, "tower", "stack up", "stack", "pyramid", "teamwork"))
        {
            if (BuddyTower.Current) return Pick("ALREADY STACKING!", "WE'RE ON IT!");
            int friends = 0;
            foreach (var o in GummyBody.All) if (o && o != b && o.isActiveAndEnabled) friends++;
            if (friends == 0) return Pick("A TOWER OF ONE?", "NEED FRIENDS FOR THAT");
            close = true;
            action = () => { if (!BuddyTower.Show(b) && speech) speech.Say(Pick("NOBODY'S FREE", "THEY'RE BUSY :(", "NO ROOM HERE"), 0f, true); };
            return V(v, "TOWER TIME!", "STACK UP, TEAM!", "BEAN STACK!", "BURGER TOWER!", "DINO STACK!");
        }
        if (Has(m, "stomp", "super stomp", "earthquake", "ground pound"))
        {
            var st = b.GetComponent<BuddyStomp>();
            var sk = b.GetComponent<BuddySkills>();
            if (!st || (sk && sk.Gear != BuddySkills.Kit.Stomp)) return V(v, "DINO DOES THAT", "STOMP? I HOP", "ASK DINO", "I'D SQUASH MY PATTY");
            if (!st.Has) return sk ? $"AT LEVEL {sk.LevelFor(BuddySkills.Skill.Gadget)}!" : "SOMEDAY!";
            if (st.Stomp()) { close = true; return Pick("STAND BACK!", "RAWR! STOMP!", "EARTHQUAKE!"); }
            return Pick("IN A SEC!", "I'M BUSY STOMPING");
        }
        if (Has(m, "grind", "grinding", "rail", "rails", "grind the rails"))
        {
            var sk = b.GetComponent<BuddySkills>();
            if (sk && !sk.Has(BuddySkills.Skill.Grind)) return $"AT LEVEL {sk.LevelFor(BuddySkills.Skill.Grind)}!";
            if (!b.GetComponent<BuddySkate>()) return Pick("NO BOARD :(", "WHAT SKATEBOARD?");
            if (brain && brain.Current != GummyBrain.State.Idle) return Pick("HOLD ON!", "IN A SEC!");
            if (brain && brain.GrindNow()) { close = true; return V(v, "SPARKS INCOMING!", "SHRED TIME!", "GRIND-PRESSO!"); }
            return Pick("NO RAILS NEAR", "NEED A WINDOW TO GRIND");
        }
        if (Has(m, "skate", "skateboard", "skating", "kickflip", "ollie", "do a trick", "tony hawk"))
        {
            var sk = b.GetComponent<BuddySkills>();
            if (sk && !sk.Has(BuddySkills.Skill.Skate)) return $"AT LEVEL {sk.LevelFor(BuddySkills.Skill.Skate)}!";
            if (!b.GetComponent<BuddySkate>()) return Pick("NO BOARD :(", "WHAT SKATEBOARD?");
            if (brain && brain.Current != GummyBrain.State.Idle) return Pick("HOLD ON!", "IN A SEC!");
            bool trick = Has(m, "kickflip", "ollie", "do a trick", "trick", "tricks");
            if (brain && brain.SkateNow(trick))
            {
                close = true;
                if (trick && sk && !sk.Ollies) return $"TRICKS AT LEVEL {sk.LevelFor(BuddySkills.Skill.Skate) + 1}!";
                return trick ? V(v, "WATCH THIS!", "SICK TRICK TIME", "FLIP-PRESSO!") : V(v, "SKATE TIME!", "RADICAL!", "BEAN SPEED!");
            }
            return Pick("NO ROOM TO RIDE", "TOO SHORT HERE");
        }
        // for finding drawing bugs: writes whipit_faces.txt next to the saves
        if (Has(m, "debug faces", "debug face", "debug eyes"))
        {
            action = () => FaceDebug.Dump();
            return "WROTE WHIPIT_FACES.TXT";
        }
        // the dino roars back (the others: eek)
        if (Has(m, "rawr", "rawrr", "rawrrr", "roar", "rarr"))
        {
            var dino = b.GetComponent<BuddyDino>();
            if (dino) { action = () => dino.Roar(false); return Pick("RAWR!!", "RAWRRRR!", "RAWR! SCARED?"); }
            return Here(BuddySpeech.Voice.Dino) ? V(v, "EEK! DINO?", "AAAH! ...OH, IT'S YOU", "ROAR-PRESSO!", "EEK... I MEAN HI")
                                                : V(v, "RAWR? I'M AN APPLE", "RAWR!! HEHE", "ROAR-PRESSO!", "RAWR... BURP");
        }
        // background mode (Occlusion): live in the desktop behind your windows, or always in front
        if (Has(m, "on top", "come out", "stay on top", "always on top", "in front", "come to the front"))
        {
            if (!Occlusion.Enabled) return Pick("I'M ALREADY IN FRONT", "HERE I AM!");
            action = () => Occlusion.Enabled = false;
            return V(v, "OK, ALWAYS IN FRONT!", "FRONT ROW!", "IN FRONT. ALWAYS.", "FRONT AND CENTER");
        }
        if (Has(m, "background", "go behind", "behind windows", "behind the windows", "go back", "hide behind windows"))
        {
            if (Occlusion.Enabled) return Pick("I'M ALREADY BACK HERE", "ALREADY BEHIND");
            action = () => Occlusion.Enabled = true;
            return V(v, "BACK TO MY DESKTOP", "OK, BEHIND THE WINDOWS", "BACKGROUND MODE", "BACK TO THE GRILL");
        }
        if (LaserPointer.Current && Has(m, "laser off", "no laser", "stop laser", "stop the laser", "laser away", "turn off the laser"))
        {
            if (!LaserPointer.On) return Pick("IT'S OFF", "WHAT LASER?");
            action = () => LaserPointer.Current.Turn(false);
            return V(v, "AWW, OK", "NOOO MY DOT", "LASER: OFF");
        }
        if (LaserPointer.Current && Has(m, "laser", "laser pointer", "red dot", "the dot", "dot"))
        {
            if (LaserPointer.On) return V(v, "WHERE IS IT?!", "I SEE IT!", "TRACKING...");
            action = () => LaserPointer.Current.Turn(true);
            close = true;
            return V(v, "A DOT?! WHERE?!", "RED DOT TIME!", "DOT MODE: ON");
        }
        if (Has(m, "balloon", "float up", "float", "up up and away"))
        {
            var sk = b.GetComponent<BuddySkills>();
            if (sk && !sk.Has(BuddySkills.Skill.Balloon)) return $"AT LEVEL {sk.LevelFor(BuddySkills.Skill.Balloon)}!";
            if (!b.GetComponent<BuddyBalloon>()) return Pick("NO BALLOONS :(", "OUT OF BALLOONS");
            if (brain && brain.Current != GummyBrain.State.Idle) return Pick("HOLD ON!", "IN A SEC!");
            if (brain && brain.FloatNow()) { close = true; return V(v, "UP UP AND AWAY!", "BALLOON TIME!", "HOT AIR BEAN, GO!"); }
            return Pick("NOTHING UP THERE", "NO ROOM UP THERE", "NOWHERE TO FLOAT");
        }
        if (Has(m, "fly", "paper plane", "glide", "airplane", "plane", "fly away"))
        {
            var sk = b.GetComponent<BuddySkills>();
            if (sk && !sk.Has(BuddySkills.Skill.Glide)) return $"AT LEVEL {sk.LevelFor(BuddySkills.Skill.Glide)}!";
            if (!b.GetComponent<BuddyPlane>()) return Pick("NO PAPER :(", "I CAN'T FOLD");
            if (brain && brain.Current != GummyBrain.State.Idle) return Pick("HOLD ON!", "IN A SEC!");
            if (brain && brain.GlideNow()) { close = true; return V(v, "WATCH ME FLY!", "AIRMAIL TIME!", "BEAN-747, TAKING OFF"); }
            return b.OnPlatform ? Pick("NOWHERE TO LAND", "TOO LOW HERE") : Pick("PUT ME UP HIGH!", "NEED A WINDOW TOP");
        }
        // the candy cloud (CandyCloud: Ctrl+Shift+K)
        if (Has(m, "candy rain", "candy cloud", "rain candy", "candy shower", "candy storm", "candies", "sweets"))
        {
            if (!CandyCloud.Instance) return Pick("NO CLOUDS TODAY", "SKY'S EMPTY");
            close = true;
            action = () => { if (CandyCloud.Instance) CandyCloud.Instance.Shower(); };
            return V(v, "CANDY TIME!", "MAKE IT RAIN!", "SUGAR INCOMING!", "DESSERT FROM THE SKY!", "RAWR! CANDY!");
        }
        // the rain (Rain: Ctrl+Shift+R) and the puddles (BuddyRain)
        // caught in the act (CaughtInTheAct): what they get up to while you're away
        if (CaughtInTheAct.Current && Has(m, "go wild", "party while im away", "party when im away", "what do you do when im away", "what do you do when im gone", "what do you do while im away", "do you party", "party without me"))
        {
            action = () => { if (CaughtInTheAct.Current) CaughtInTheAct.Current.Dare(); };
            return v == BuddySpeech.Voice.Duck ? "QUACK? NOTHING!" : V(v, "NOTHING! WE JUST... SIT", "PARTY? US? NEVER", "WE NAP. TOTALLY.", "NOTHING. BUN-HONEST", "RAWR? WE ARE GOOD");
        }
        // the trophy shelf (Trophies)
        if (Trophies.Current && Has(m, "trophies", "trophy", "trophy shelf", "badges", "medals", "awards", "my medals", "my trophies", "show trophies", "show medals", "achievements"))
        {
            close = true;
            if (Trophies.Current.Showing) { action = () => { if (Trophies.Current) Trophies.Current.Hide(); }; return Pick("OK, PUT AWAY!", "SHOWING OFF OVER"); }
            action = () => { if (Trophies.Current) Trophies.Current.Show(b); };
            return v == BuddySpeech.Voice.Duck ? "QUACK! LOOK!" : V(v, "OUR TROPHIES!", "WANNA SEE MY MEDALS?", "HALL OF FAME!", "THE BUN SHELF!", "RAWR! TROPHIES!");
        }
        // the giant potion (GiantPotion)
        if (GiantPotion.Current && GiantPotion.Current.Giant && Has(m, "shrink", "no potion", "small again", "normal size", "back to normal", "unshrink", "tiny again"))
        {
            close = true;
            action = () => { if (GiantPotion.Current) GiantPotion.Current.PopNow(); };
            return GiantPotion.Current.Giant == b ? V(v, "AWW, OK...", "FINE. SMALL AGAIN", "SHRINK RAY? OK", "BACK TO SLIDER SIZE", "RAWR... OK") : Pick("POP!", "AWW, IT WAS FUN!");
        }
        if (GiantPotion.Current && Has(m, "potion", "giant potion", "magic potion", "make me giant", "make me big", "giant", "grow big", "get big", "supersize", "super size"))
        {
            string why = GiantPotion.Current.Blocked();
            if (why == "giant") return GiantPotion.Current.Giant == b ? V(v, "I'M ALREADY HUGE!", "LOOK AT ME!", "MAX SIZE!", "ALREADY XXL!", "RAWR! ALREADY BIG!") : Pick("ONE GIANT AT A TIME!", "LOOK UP!");
            if (why != null) return Pick("THERE'S ONE ALREADY!", "GRAB THAT ONE!");
            close = true;
            action = () => { if (GiantPotion.Current) GiantPotion.Current.DropFor(b); };
            return v == BuddySpeech.Voice.Duck ? "QUACK! SHINY!" : V(v, "ME! I WANT TO BE BIG!", "GIANT TOMATO TIME?", "A BREW? FOR ME?", "SUPERSIZE ME!", "RAWR! KAIJU TIME!");
        }
        // the floor is lava (Lava)
        if (Lava.Current && Has(m, "no lava", "stop lava", "stop the lava", "lava off", "enough lava", "cool it down", "no more lava"))
        {
            if (!Lava.Current.On) return Pick("THE FLOOR IS JUST FLOOR", "NO LAVA HERE");
            action = () => { if (Lava.Current) Lava.Current.Stop(); };
            return v == BuddySpeech.Voice.Duck ? "AWW. QUACK." : V(v, "PHEW!", "THANK YOU!", "COOLING DOWN...", "BUN SAVED!", "RAWR... OK");
        }
        if (Lava.Current && Has(m, "lava", "floor is lava", "the floor is lava", "hot floor", "lava game", "play lava"))
        {
            if (Lava.Current.On) return Pick("IT ALREADY IS!", "GET UP! GET UP!");
            string why = Lava.Blocked();
            if (why == "bedtime") return "ZZZ... LATER";
            if (why == "rain") return Pick("THE RAIN WOULD COOL IT", "AFTER THE RAIN!");
            if (why != null) return Pick("NOT NOW!", "IN A BIT!");
            close = true;
            action = () => { if (Lava.Current) Lava.Current.Begin(b); };
            return v == BuddySpeech.Voice.Duck ? "QUACK! READY?" : V(v, "READY?", "OH NO... READY?", "GAME ON!", "GULP. READY?", "RAWR! VOLCANO!");
        }
        if (Rain.Current && Has(m, "stop the rain", "stop raining", "no rain", "no more rain", "rain stop", "rain off", "sunshine", "make it stop", "sun please"))
        {
            if (!Rain.Current.Raining) return v == BuddySpeech.Voice.Duck ? "IT ALREADY STOPPED :(" : Pick("IT'S NOT RAINING", "ALREADY DRY!");
            action = () => { if (Rain.Current) Rain.Current.End(); };
            return v == BuddySpeech.Voice.Duck ? "NOOO! MY RAIN!" : V(v, "YES! SUN!", "FINALLY!", "DRY BEANS AGAIN!", "SAVE THE BUN!", "RAWR! THANK YOU!");
        }
        if (Rain.Current && Has(m, "rain", "make it rain", "rainy", "raining", "rain clouds", "storm", "shower", "rain please", "let it rain"))
        {
            if (Rain.Current.Raining) return v == BuddySpeech.Voice.Duck ? "MORE! QUACK!" : Pick("IT IS RAINING!", "LOOK OUTSIDE!");
            action = () => { if (Rain.Current) Rain.Current.Begin(); };
            return v == BuddySpeech.Voice.Duck ? "RAIN?! YAY! QUACK!" : V(v, "UH OH, CLOUDS!", "NOT THE RAIN!", "WATERED DOWN?!", "MY BUN!", "RAWR! NO BATH!");
        }
        if (Rain.Current && Has(m, "puddle", "puddles", "splash", "jump in the puddle", "splash around"))
        {
            if (Rain.Current.Puddles.Count == 0) return Pick("NO PUDDLES...", "MAKE IT RAIN FIRST!");
            var br = b.GetComponent<BuddyRain>();
            if (!br || !br.SplashNow()) return Pick("I CAN'T GET THERE", "IN A SEC!", "TOO FAR!");
            close = true;
            return v == BuddySpeech.Voice.Duck ? "PUDDLES! QUACK!" : V(v, "SPLASH TIME!", "PUDDLE JUMP!", "SPLOOSH!", "BELLY FLOP!", "STOMP STOMP!");
        }
        // the ball pit (BallPit, BuddyPit) and the slide (Slide, BuddySlide)
        if (Has(m, "no ball pit", "no pit", "pit away", "ball pit away", "take away the ball pit", "no more balls"))
        {
            if (!BallPit.Current) return Pick("WHAT BALL PIT?", "NO PIT OUT");
            action = () => BallPit.TakeDown();
            return V(v, "AWW. BYE BALLS", "NOOO! THE BALLS!", "FINE. DRY LAND", "BYE BYE BALLS", "RAWR... OK");
        }
        if (Has(m, "cannonball", "cannon ball", "jump in", "dive in", "dive"))
        {
            var bp = b.GetComponent<BuddyPit>();
            if (!BallPit.Current || !bp) return Pick("SAY BALL PIT FIRST!", "INTO WHAT?");
            close = true;
            action = () => { if (!bp.CannonballNow() && speech) speech.Say(Pick("CAN'T GET UP THERE", "IN A SEC!"), 0f, true); };
            return V(v, "WATCH THIS!", "CANNONBALL!", "BEAN BOMB INCOMING", "STAND BACK!", "RAWR! WATCH!");
        }
        if (Has(m, "ball pit", "ballpit", "pit", "ball pool", "pool of balls", "balls"))
        {
            if (BallPit.Current) return V(v, "SAY CANNONBALL!", "JUMP IN!", "BALLS!", "SAY CANNONBALL", "RAWR! BALLS!");
            close = true;
            action = () => Playground.PutPit(b.spriteMaterial, Grabber.CursorWorld);
            return V(v, "BALL PIT!", "BALLS! SO MANY!", "BALL BATH!", "BALL PIT PARTY!", "RAWR! BALLS!");
        }
        if (Has(m, "no slide", "slide away", "take down the slide", "no more sliding"))
        {
            if (!Slide.Current) return Pick("WHAT SLIDE?", "NO SLIDE UP");
            action = () => Slide.TakeDown();
            return V(v, "AWW. NO MORE WHEEE", "NOOO! MY SLIDE!", "FINE. STAIRS IT IS", "BYE SLIDE", "RAWR... OK");
        }
        if (Has(m, "go slide", "slide down", "go down the slide", "wheee", "go sliding", "ride the slide"))
        {
            var bs = b.GetComponent<BuddySlide>();
            if (!Slide.Current || !bs) return Pick("SAY SLIDE FIRST!", "WHAT SLIDE?");
            close = true;
            action = () => { if (!bs.SlideNow() && speech) speech.Say(Pick("CAN'T GET THERE", "IN A SEC!"), 0f, true); };
            return V(v, "WHEEE!", "ON MY WAY!", "SLIDE-PRESSO!", "HERE I GO!", "RAWR! WHEEE!");
        }
        if (Has(m, "slide", "slides", "playground", "slippery slide", "water slide"))
        {
            if (Slide.Current) return V(v, "SAY GO SLIDE!", "WHEEE! SAY GO SLIDE", "SAY GO SLIDE", "SAY GO SLIDE!", "RAWR! GO SLIDE!");
            close = true;
            action = () => Playground.PutSlide(b.spriteMaterial, Grabber.CursorWorld);
            return V(v, "A SLIDE!", "SLIDE TIME!", "WHEEE INCOMING!", "PLAYGROUND!", "RAWR! SLIDE!");
        }
        // basketball (Hoop, Basketball, BuddyHoops, PickupGame)
        var hoops = b.GetComponent<BuddyHoops>();
        if (Has(m, "no hoop", "hoop away", "take down the hoop", "no more basketball", "no basketball", "hoop down"))
        {
            if (!Hoop.Current) return Pick("WHAT HOOP?", "NO HOOP UP");
            action = () => Hoop.TakeDown();
            return V(v, "AWW. OK", "FINE. NO HOOPS", "BACK TO WORK?", "BYE BYE HOOP", "RAWR... OK");
        }
        if (Has(m, "basketball", "hoops", "hoop", "shoot hoops", "b-ball", "bball"))
        {
            if (Hoop.Current) return Hoop.Current.Playable ? V(v, "HOOP'S UP! SAY GAME", "SAY GAME! 1V1 ME", "GAME? SAY GAME!", "SAY DUNK!", "SAY GAME! RAWR") : Pick("PUT IT SOMEWHERE!", "IT FELL!");
            close = true;
            action = () => Hoop.Put(b.spriteMaterial, Grabber.CursorWorld);
            return V(v, "HOOP TIME!", "BALL IS LIFE!", "HOOPS-PRESSO!", "LET ME DUNK", "RAWR! HOOPS!");
        }
        if (Has(m, "game", "pickup", "pickup game", "1v1", "2v2", "play basketball", "match"))
        {
            if (!Hoop.Current) return Pick("PUT THE HOOP UP FIRST", "SAY BASKETBALL FIRST!");
            if (PickupGame.Current) return Pick("WE'RE PLAYING!", "ALREADY ON!");
            close = true;
            action = () => { if (!PickupGame.TryStart(hoops, true) && speech) speech.Say(Pick("NOBODY TO PLAY", "NEED A FRIEND", "THEY'RE BUSY"), 0f, true); };
            return V(v, "YOU'RE ON!", "GAME ON!", "LET'S GOOO!", "BALL IS LIFE", "RAWR! GAME!");
        }
        if (Has(m, "dunk", "slam dunk", "slam", "dunk it"))
        {
            if (!Hoop.Current || !hoops) return Pick("PUT THE HOOP UP FIRST", "SAY BASKETBALL FIRST!");
            if (PickupGame.Current) return Pick("WE'RE PLAYING!", "IN THE GAME!");
            if (hoops.CanDunk(false, out _) == BuddyHoops.Dunk.None) return V(v, "TOO HIGH FOR ME", "CAN'T REACH. YET", "TOO HIGH. MORE COFFEE", "I CAN'T JUMP THAT", "TINY ARMS. TOO HIGH");
            close = true;
            action = () => { if (!hoops.DunkNow() && speech) speech.Say(Pick("IN A SEC!", "WHERE'S THE BALL?"), 0f, true); };
            return V(v, "WATCH THIS!", "SLAM TIME!", "STAND BACK!", "HOLD MY BUN", "RAWR! DUNK!");
        }
        if (Has(m, "shoot", "shoot it", "take a shot", "three", "shoot the ball"))
        {
            if (!Hoop.Current || !hoops) return Pick("SHOOT WHAT?", "NO HOOP UP");
            if (PickupGame.Current) return Pick("WE'RE PLAYING!", "IN THE GAME!");
            close = true;
            action = () => { if (!hoops.ShootNow() && speech) speech.Say(Pick("IN A SEC!", "WHERE'S THE BALL?"), 0f, true); };
            return V(v, "BUCKETS INCOMING", "WATCH THIS!", "SWISH TIME", "FROM DOWNTOWN!", "RAWR! SHOT!");
        }
        if (Has(m, "fetch", "ball", "play ball", "play fetch", "throw the ball"))
        {
            if (FetchBall.Current && !FetchBall.Current.InPlay) return V(v, "THROW IT!", "THROW IT! I'M READY", "THROW IT! FAST!");
            if (FetchBall.Current) return Pick("I'M ON IT!", "GETTING IT!");
            action = () =>
            {
                var d = UnityEngine.Object.FindFirstObjectByType<SnackDropper>();
                Rect screen = ScreenBounds.ScreenRectAt(Grabber.CursorWorld); // (more than one screen: the cursor's)
                float x = Mathf.Clamp(Grabber.CursorWorld.x, screen.xMin + 0.5f, screen.xMax - 0.5f);
                FetchBall.Spawn(new Vector2(x, screen.yMax - 0.2f), new Vector2(UnityEngine.Random.Range(-1f, 1f), 0f), d ? d.material : null, d ? d.sortingOrder : 8, false);
            };
            close = true;
            return V(v, "BALL! THROW IT!", "FETCH? I'M FASTER", "FETCH! GRAB IT & THROW");
        }
        if (Has(m, "zipline", "zip line", "zip", "zip down", "zipline down"))
        {
            var sk = b.GetComponent<BuddySkills>();
            if (sk && !sk.RopeKit) return Pick("APPLE STRINGS THEM. I RIDE!", "ASK APPLE! I'LL RIDE IT");
            if (sk && !sk.Has(BuddySkills.Skill.Zip)) return $"AT LEVEL {sk.LevelFor(BuddySkills.Skill.Zip)}!";
            if (brain && brain.Current != GummyBrain.State.Idle) return Pick("HOLD ON!", "IN A SEC!");
            if (brain && brain.ZipNow()) { close = true; return V(v, "ZIPLINE TIME!", "WHEEE, TOMATO MAIL!", "EXPRESS DELIVERY!"); }
            return b.OnPlatform ? Pick("NOWHERE TO ZIP TO", "NO ROOM HERE") : Pick("PUT ME UP HIGH!", "NEED A HIGH WINDOW");
        }
        if (Has(m, "swing", "tarzan", "swing around"))
        {
            var sk = b.GetComponent<BuddySkills>();
            if (sk && !sk.RopeKit) return Pick("ONLY APPLE HAS A ROPE", "NO ROPE, NO SWING");
            if (sk && !sk.Has(BuddySkills.Skill.Swing)) return $"AT LEVEL {sk.LevelFor(BuddySkills.Skill.Swing)}!";
            if (brain && brain.Current != GummyBrain.State.Idle) return Pick("HOLD ON!", "IN A SEC!");
            if (brain && brain.SwingNow()) { close = true; return V(v, "WATCH THIS!", "TARZAN TIME!", "WHEEE!"); }
            return Pick("NOTHING TO SWING ON", "NEED A WINDOW UP HIGH");
        }
        if (Has(m, "whip", "crack", "yeehaw"))
        {
            if (whip && whip.Has) { action = () => whip.Crack(null, null); return Pick("YEEHAW!", "WATCH THIS", "HUP!"); }
            if (tomato) return Pick("APPLE GETS IT", "NOT MINE");
            if (bean) return Pick("APPLE'S THING", "I'D SPILL");
            return whip ? $"AT LEVEL {whip.unlockLevel}!" : "WHAT WHIP?";
        }
        var dodge = b.GetComponent<BuddyDodge>();
        if (dodge && Has(m, "stop dodging", "dont dodge", "don't dodge", "no dodging", "hold still", "stay still"))
        {
            action = () => dodge.Behave(120f);
            return V(v, "OK, I'LL BE GOOD", "FINE. GRAB ME", "OK OK. STILL.");
        }
        if (dodge && Has(m, "dodge", "dodge me", "try to dodge", "bet you cant dodge", "bet you can't dodge"))
        {
            var sk = b.GetComponent<BuddySkills>();
            if (sk && sk.Level < dodge.fromLevel) return $"AT LEVEL {dodge.fromLevel}!";
            action = () => dodge.Tease(30f);
            return V(v, "TRY AND CATCH ME!", "YOU'LL NEVER CATCH ME", "TOO FAST FOR U!");
        }
        if (Has(m, "tag", "chase", "catch me", "race"))
        {
            if (!hasFriend) return Pick("WITH WHO?", "NO ONE TO CHASE");
            action = () => BuddySocial.PlayTag(b);
            close = true;
            return bean ? Pick("ZOOM! YOU'RE IT", "TOO FAST FOR U") : Pick("TAG! YOU'RE IT", "RACE YA!", "HEHE OK");
        }
        if (Has(m, "boo"))
            return tomato ? Pick("AAAH!", "NOT SCARED") : bean ? Pick("EEEK! SPILLED", "BOO TO YOU!") : Pick("AAAH!", "BOO TO YOU!", "YOU SCARED ME");

        if (Has(m, "snack", "food", "hungry", "eat", "cookie", "candy", "treat", "feed"))
        {
            action = () => { var d = UnityEngine.Object.FindFirstObjectByType<SnackDropper>(); if (d) d.Drop(true); };
            return bean ? Pick("FUEL! YES PLS", "SNACK BREAK!") : Pick("YES PLS!", "FOOD!!", "NOM TIME", "I'M ALWAYS HUNGRY");
        }
        if (Has(m, "sleep", "nap", "good night", "goodnight", "night night", "bed"))
        {
            action = () => { if (brain) brain.NapNow(); };
            close = true;
            return bean ? Pick("DECAF MODE...", "FINE. 5 MIN") : Pick("ZZZ...", "NIGHT!", "5 MIN NAP");
        }
        if (Has(m, "jump", "hop", "dance", "spin", "bounce"))
        {
            action = () => { if (b.Grounded && !b.Held) b.Hop(new Vector2(0f, 7f * Mathf.Sqrt(b.size / 0.6f)), 0.8f); };
            return Pick("WHEE!", "BOING!", "LIKE THIS?");
        }
        if (Has(m, "come here", "come", "over here", "here boy", "here girl"))
        {
            action = () => { if (brain) brain.Visit(Grabber.CursorWorld.x); };
            return Pick("COMING!", "ON MY WAY", "OK OK");
        }

        // talk
        if (Has(m, "bye", "cya", "see you", "see ya", "gtg", "adijo"))
        {
            close = true;
            return Pick("BYE!", "SEE YA!", "BYE ♥");
        }
        if (Has(m, "how are you", "how r u", "how are u", "hru", "how's it going", "hows it going", "you ok", "u ok", "feeling"))
            return Mood(brain, v);
        if (Has(m, "your name", "who are you", "who r u", "what are you", "what r u"))
            return V(v, "I'M APPLE!", "TOMATO. OBVIOUSLY", "COFFEE BEAN. FULL ROAST", "BURGER. EXTRA CHEESE", "A DINO! RAWR!");
        r = AboutFriends(m, v, progress);
        if (r != null) return r;
        if (Has(m, "love", "<3", "♥", "cute", "good boy", "good girl", "best", "adorable", "sweet", "awesome", "nice", "cool"))
        {
            action = () => { if (brain) brain.Treat(); };
            return Pick("♥", "AWW", "HEHE ♥", tomato ? "I KNOW" : bean ? "U R ESPRESSO-LY NICE" : "YOU TOO!");
        }
        if (Has(m, "bad", "hate", "stupid", "dumb", "ugly", "shut up", "annoying"))
            return tomato ? Pick("GRR", "RUDE!", "SAYS YOU") : bean ? Pick("BITTER!", "RUDE!", "THAT'S HARSH") : Pick("RUDE!", "HMPH", ":(");
        if (Has(m, "thanks", "thank you", "thx", "ty"))
            return Pick("ANYTIME!", "♥", "NP!");
        if (Has(m, "good morning", "morning", "good afternoon", "afternoon", "good evening", "evening"))
            return TimeGreeting(m, v);
        if (Has(m, "hi", "hello", "hey", "yo", "sup", "hiya", "howdy", "hallo", "hej", "zdravo", "živjo", "zivjo", "ojla"))
        {
            string n = ChatMemory.Show(ChatMemory.Name, 10);
            if (UnityEngine.Random.value < 0.5f) return Greeting(v);
            if (n.Length > 0) return V(v, $"HI {n}!", $"YO {n}", $"HEY {n}!");
            return tomato ? Pick("HI!!", "SUP", "YO!") : bean ? Pick("HI HI HI!", "HEY!", "O/") : Pick("HI!!", "HEY YOU!", "HELLO!");
        }
        if (Has(m, "bug", "bugs", "error", "errors", "crash", "broken", "fix", "exception"))
            return bean ? Pick("MORE COFFEE, LESS BUGS", "YOU GOT THIS!", "DEBUG BREW TIME") : Pick("YOU GOT THIS!", "TURN IT OFF AND ON?", "I'LL EAT IT", "BLAME THE TOMATO");
        if (bean && Has(m, "coffee", "espresso", "latte", "caffeine", "cappuccino"))
            return Pick("I AM COFFEE!", "DON'T DRINK ME!", "WE'RE FAMILY");
        if (Has(m, "work", "code", "coding", "unity", "build", "compile", "shader", "commit", "deadline"))
            return bean ? Pick("LET'S GRIND!", "FOCUS MODE ON", "SHIP IT!") : Pick("SHIP IT!", "TAKE BREAKS!", "UNITY? I LIVE THERE", "NICE CODE");
        if (Has(m, "break", "coffee", "tired", "water", "lunch"))
            return bean ? Pick("BREAK? YES PLS", "STRETCH!", "REFILL TIME") : Pick("BREAK TIME!", "DRINK WATER", "STRETCH!");
        if (Has(m, "lol", "haha", "hehe", "lmao", "xd"))
            return Pick("HEHE", "LOL", "HAHA");
        if (Has(m, "yes", "yeah", "yep", "ok", "okay", "sure", "yup"))
            return Pick("YAY!", "OK!", "COOL");
        if (Has(m, "no", "nope", "nah"))
            return Pick("AW", "FINE", "HMPH");
        if (question)
        {
            string friend = SomeFriend(b);
            return Pick("HMM...", "MAYBE?", friend != null ? $"ASK {friend}" : "ASK ME LATER", "42", "NO IDEA!");
        }

        // no idea what you mean: sometimes echo the last word back
        string last = LastWord(message);
        if (last.Length >= 3 && last.Length <= 10 && UnityEngine.Random.value < 0.4f) return last.ToUpperInvariant() + "?";
        return Pick("HUH?", "OK!", "COOL", "TELL ME MORE", V(v, "I'M JUST AN APPLE", "I'M JUST A TOMATO", "I'M JUST A BEAN"));
    }

    // ------------------------------------------------------------------ you (ChatMemory)

    static readonly string[] NotNames =
    {
        "not", "a", "an", "the", "so", "very", "just", "really", "is", "your", "you", "what", "still", "fine", "good", "ok",
        "okay", "back", "here", "done", "tired", "hungry", "busy", "sorry", "bored", "working", "going", "sure", "glad", "happy",
        "sad", "excited", "ready", "in", "on", "at", "home", "coding", "sick", "late", "free", "out", "off", "too", "also", "maybe"
    };

    static readonly string[] Stops = { "and", "but", "so", "because", "cause", "too", "very", "much", "lol", "haha", "though", "really", "a lot", "alot", "please", "pls", "ok" };

    static string AboutYou(string m, string raw, BuddySpeech.Voice v, bool question)
    {
        // forgetting
        if (Has(m, "forget everything", "forget me", "forget all", "forget it all", "forget all of it", "delete everything"))
        {
            ChatMemory.Forget();
            return V(v, "FORGOT! WHO R U? :)", "WHO? NEVER MET YOU", "MEMORY WIPED. DECAF");
        }
        if (Has(m, "forget my name"))
        {
            ChatMemory.ForgetName();
            return V(v, "OK, MYSTERY FRIEND", "FINE, STRANGER", "NAME? WHAT NAME?");
        }

        // your name
        if (Has(m, "what's my name", "whats my name", "what is my name", "who am i", "do you know my name", "do you know me", "remember me", "remember my name", "do you remember me"))
        {
            string known = ChatMemory.Show(ChatMemory.Name, 12);
            if (known.Length == 0) return V(v, "YOU NEVER SAID! TELL ME?", "NO IDEA. WHO R U?", "A MYSTERY HUMAN");
            return V(v, $"{known}! ♥", $"{known}. OBVIOUSLY", $"{known}! MY FAVE HUMAN");
        }
        string name = Word(m, "my name is", "my name's", "call me", "i am called", "i'm called", "im called", "name's");
        if (name == null) name = CapitalAfterIAm(raw);
        if (name != null && IsName(name))
        {
            name = Original(raw, name);
            ChatMemory.SetName(name);
            string n = ChatMemory.Show(name, 12);
            return V(v, $"HI {n}! ♥", $"{n}? OK. NICE NAME", $"{n}! NICE TO MEET YOU");
        }

        // what you're working on
        if (Has(m, "what am i working on", "what i'm working on", "what im working on", "my project", "what's my project", "whats my project"))
        {
            string p = ChatMemory.Show(ChatMemory.WorkingOn, 16);
            return p.Length > 0 ? V(v, $"{p}! YOU GOT THIS", $"{p}. STILL", $"{p}! GRIND ON") : V(v, "NO IDEA. TELL ME?", "DUNNO. NERD STUFF?", "TELL ME! I'LL CHEER");
        }
        string proj = Phrase(m, 4, "i'm working on", "im working on", "i am working on", "i work on", "working on");
        if (!question && proj != null && !Starts(proj, "it", "this", "that", "stuff", "something", "things", "a lot"))
        {
            ChatMemory.SetWorkingOn(proj);
            string p = ChatMemory.Show(proj, 16);
            return V(v, $"{p}? YOU GOT THIS!", $"{p}? SOUNDS HARD. HEHE", $"{p}! LET'S GRIND!");
        }

        // favourites
        foreach (string fav in new[] { "favorite", "favourite", "fave", "fav" })
        {
            if (!m.Contains(" " + fav + " ")) continue;
            if (Has(m, "your " + fav)) return V(v, "SNACKS. ALL OF THEM", "WINNING. AND CANDY", "ESPRESSO. OBVIOUSLY");
            string what = Between(m, " my " + fav + " ", " is ", " are ");
            if (what == null)
            {
                // "what's my favorite food?"
                string asked = Phrase(m, 2, "my " + fav);
                if (asked == null) break;
                string val = ChatMemory.Favorite(asked);
                string w = ChatMemory.Show(asked, 10);
                return val != null ? V(v, $"{ChatMemory.Show(val, 14)}!", $"{ChatMemory.Show(val, 14)}. EASY", $"{ChatMemory.Show(val, 14)}! I REMEMBER")
                                   : V(v, $"YOU NEVER TOLD ME!", $"YOUR {w}? NO CLUE", $"TELL ME YOUR {w}!");
            }
            string value = Phrase(m, 3, "my " + fav + " " + what + " is", "my " + fav + " " + what + " are");
            if (value == null || question) break;
            ChatMemory.SetFavorite(what, value);
            string sv = ChatMemory.Show(value, 14);
            return V(v, $"{sv}! GOOD CHOICE", $"{sv}? HMM OK", $"{sv}! NOTED");
        }

        // what you like / don't like
        if (Has(m, "what do i like", "what i like", "what do i love"))
        {
            var likes = ChatMemory.Likes;
            if (likes.Count == 0) return V(v, "YOU NEVER SAID!", "DUNNO. SNACKS?", "COFFEE? :)");
            string l = ChatMemory.Show(likes[UnityEngine.Random.Range(0, likes.Count)], 14);
            return V(v, $"{l}! ♥", $"{l}. NERD", $"{l}!");
        }
        if (Has(m, "what do i hate", "what don't i like", "what dont i like"))
        {
            var no = ChatMemory.Dislikes;
            if (no.Count == 0) return V(v, "NOTHING I KNOW OF", "TOMATOES? HEHE", "DECAF?");
            return $"{ChatMemory.Show(no[UnityEngine.Random.Range(0, no.Count)], 14)}!";
        }
        if (!question)
        {
            string like = Phrase(m, 3, "i really like", "i really love", "i like", "i love", "i enjoy", "i adore");
            if (like != null && like.StartsWith("to ")) like = like.Substring(3);
            if (like != null && like.Length > 1 && !Starts(like, "you", "u", "ya", "your", "it", "this", "that", "them", "him", "her", "when", "how", "the way"))
            {
                ChatMemory.AddLike(like);
                string l = ChatMemory.Show(like, 14);
                return V(v, $"{l}? ME TOO! ♥", $"{l}? OK, NOTED", $"{l}! NOTED");
            }
            string hate = Phrase(m, 3, "i hate", "i don't like", "i dont like", "i do not like", "i can't stand", "i cant stand");
            if (hate != null && !Starts(hate, "you", "u", "ya", "your"))
            {
                ChatMemory.AddDislike(hate);
                string h = ChatMemory.Show(hate, 14);
                return V(v, $"NO {h}. GOT IT", $"{h}? BOO!", $"{h}? BITTER. NOTED");
            }
        }

        // anything else to remember
        if (Has(m, "what do you remember", "what do you know about me", "what do you know", "what do u know", "tell me about me", "what did i tell you"))
            return Summary(v);
        if (!Has(m, "do you remember", "remember me", "remember my", "remember when", "i remember", "remember you"))
        {
            string note = Phrase(m, 6, "remember that", "remember");
            if (note != null)
            {
                ChatMemory.AddNote(note);
                return V(v, "OK! I'LL REMEMBER", "NOTED. MAYBE", "SAVED TO MY BEAN");
            }
        }
        return null;
    }

    // a line or two of what it knows about you
    static string Summary(BuddySpeech.Voice v)
    {
        var facts = new List<string>();
        string n = ChatMemory.Show(ChatMemory.Name, 12);
        if (n.Length > 0) facts.Add($"YOU'RE {n}");
        if (ChatMemory.WorkingOn.Length > 0) facts.Add($"YOU WORK ON {ChatMemory.Show(ChatMemory.WorkingOn, 14)}");
        if (ChatMemory.Likes.Count > 0) facts.Add($"YOU LIKE {ChatMemory.Show(ChatMemory.Likes[UnityEngine.Random.Range(0, ChatMemory.Likes.Count)], 12)}");
        var fav = ChatMemory.AnyFavorite();
        if (fav != null) facts.Add($"FAVE {ChatMemory.Show(fav[0], 8)}: {ChatMemory.Show(fav[1], 12)}");
        if (ChatMemory.Notes.Count > 0) facts.Add(ChatMemory.Show(ChatMemory.Notes[ChatMemory.Notes.Count - 1], 20));
        if (facts.Count == 0) return V(v, "NOTHING YET! TELL ME STUFF", "NOTHING. YOU'RE A MYSTERY", "EMPTY CUP. TELL ME STUFF");
        string first = facts[0];
        if (facts.Count == 1) return first + "!";
        string second = facts[UnityEngine.Random.Range(1, facts.Count)];
        return first + ". " + second;
    }

    /// <summary>Something to say about you now and then (null if it knows nothing about you yet).</summary>
    public static string PersonalLine(BuddySpeech.Voice v)
    {
        var opts = new List<string>();
        string n = ChatMemory.Show(ChatMemory.Name, 10);
        if (n.Length > 0) opts.Add(V(v, $"HI {n}! ♥", $"{n}! LOOK AT ME", $"{n}! KEEP GOING!"));
        string p = ChatMemory.Show(ChatMemory.WorkingOn, 14);
        if (p.Length > 0) opts.Add(V(v, $"HOW'S {p}?", $"{p} DONE YET?", $"{p}: LET'S GO!"));
        if (ChatMemory.Likes.Count > 0)
        {
            string l = ChatMemory.Show(ChatMemory.Likes[UnityEngine.Random.Range(0, ChatMemory.Likes.Count)], 12);
            opts.Add(V(v, $"I LIKE {l} TOO", $"{l}? STILL?", $"{l} + COFFEE?"));
        }
        return opts.Count > 0 ? opts[UnityEngine.Random.Range(0, opts.Count)] : null;
    }

    // ------------------------------------------------------------------ the time and your day (WorkClock)

    /// <summary>Good morning / afternoon / evening (with your name if it knows it), in its own voice.</summary>
    public static string Greeting(BuddySpeech.Voice v)
    {
        int h = WorkClock.Now.Hour;
        string n = ChatMemory.Show(ChatMemory.Name, 10);
        string you = n.Length > 0 ? " " + n : "";
        if (h >= 5 && h < 12) return V(v, $"GOOD MORNING{you}!", $"MORNIN'{you}. SNACKS?", $"MORNING{you}! COFFEE TIME");
        if (h >= 12 && h < 18) return V(v, $"GOOD AFTERNOON{you}!", $"AFTERNOON{you}. BORED", $"AFTERNOON{you}! 2ND CUP?");
        if (h >= 18 && h < 23) return V(v, $"GOOD EVENING{you}!", $"EVENING{you}. STILL HERE?", $"EVENING GRIND{you}?");
        return V(v, $"UP LATE{you}?", $"IT'S LATE{you}. NERD", $"SO LATE{you}! EVEN I'M SLEEPY");
    }

    // "good morning" at 3 pm gets teased
    static string TimeGreeting(string m, BuddySpeech.Voice v)
    {
        int h = WorkClock.Now.Hour;
        string t = WorkClock.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
        bool morning = Has(m, "good morning", "morning"), evening = Has(m, "good evening", "evening");
        bool wrong = (morning && (h >= 12 || h < 4)) || (evening && h >= 4 && h < 17);
        if (wrong) return V(v, $"{(morning ? "MORNING" : "EVENING")}?? IT'S {t}!", $"LOL IT'S {t}", $"IT'S {t}! MORE COFFEE?");
        return Greeting(v);
    }

    static string Stats(string m, GummyBody b, BuddySpeech.Voice v, BuddyProgress progress, GummyBrain brain)
    {
        var clock = WorkClock.Instance;
        DateTime now = WorkClock.Now;
        if (Has(m, "what time", "the time", "time is it", "what's the time", "whats the time"))
        {
            string t = now.ToString("HH:mm", CultureInfo.InvariantCulture);
            return now.Hour >= 23 || now.Hour < 5 ? V(v, $"{t}. BEDTIME?", $"{t}. GO TO BED", $"{t}. TOO LATE FOR COFFEE") : $"{t}!";
        }
        if (Has(m, "what day", "which day", "what's the date", "whats the date", "the date", "what date", "what's today", "whats today"))
            return now.ToString("dddd d MMM", CultureInfo.InvariantCulture).ToUpperInvariant() + "!";

        // its snacks
        if (Has(m, "how many snacks", "how many did you eat", "how much did you eat", "what did you eat", "snacks today", "did you eat", "how many cookies"))
        {
            int n = clock ? clock.EatenBy(b.gameObject) : 0;
            if (n == 0) return V(v, "NONE YET! COPY SOMETHING", "ZERO. FEED ME", "NO FUEL YET!");
            string all = clock && clock.Snacks > n ? $" ({clock.Snacks} ALL OF US)" : "";
            return $"{n} SNACK{(n == 1 ? "" : "S")} TODAY!{all}";
        }
        if (clock)
        {
            // how long you've been at it
            if (Has(m, "how long have i", "how long did i", "how much did i work", "how much have i worked", "work time", "screen time",
                    "time today", "worked today", "work today", "working today") ||
                (Has(m, "how long") && Has(m, "work", "worked", "working", "coding", "today", "been", "here")))
            {
                if (clock.ActiveToday < 60f) return V(v, "YOU JUST GOT HERE!", "LIKE 1 MIN. LAZY", "WARMING UP!");
                string t = WorkClock.Duration(clock.ActiveToday);
                string row = clock.Streak >= 600f ? $", {WorkClock.Duration(clock.Streak)} IN A ROW" : "";
                if (v == BuddySpeech.Voice.Bean && clock.Streak >= 90f * 60f) return $"{t} TODAY{row}. BREAK?";
                return V(v, $"{t} TODAY{row}!", $"{t} TODAY{row}. NERD", $"{t} TODAY{row}! NICE GRIND");
            }
            // breaks
            if (Has(m, "last break", "how many breaks", "breaks today", "when did i take a break", "when was my break", "my break", "had a break", "my breaks"))
            {
                if (clock.LastBreakAt < 0) return V(v, "NO BREAK YET TODAY!", "ZERO BREAKS. MACHINE", "NO BREAKS! TAKE ONE");
                string at = $"{clock.LastBreakAt / 60:00}:{clock.LastBreakAt % 60:00}";
                return $"{clock.Breaks} BREAK{(clock.Breaks == 1 ? "" : "S")}. LAST AT {at}";
            }
            if (Has(m, "how many times did i copy", "how much did i copy", "copies", "copied today", "how many copies"))
                return $"{clock.Copies} COPIES TODAY = {clock.Copies} SNACKS";
            if (Has(m, "how many times did i throw", "how many throws", "throws today", "times did you get thrown", "how many times did you fly"))
                return V(v, $"YOU THREW US {clock.Throws} TIMES", $"{clock.Throws} THROWS. RUDE", $"{clock.Throws} FLIGHTS TODAY!");
            // your day
            if (Has(m, "my day", "summary", "report", "my stats", "how am i doing", "how did i do", "stats today", "today's stats", "todays stats",
                    "how was today", "how's today", "hows today"))
            {
                string s = $"{WorkClock.Duration(clock.ActiveToday)} WORK, {clock.Breaks} BREAK{(clock.Breaks == 1 ? "" : "S")}, {clock.Snacks} SNACK{(clock.Snacks == 1 ? "" : "S")}";
                return V(v, s + " ♥", s, s + ". PROUD!");
            }
        }

        // what it can do (BuddySkills)
        var sk = b.GetComponent<BuddySkills>();
        if (sk && Has(m, "what can you do", "what can u do", "tricks", "your tricks", "skills", "your skills", "abilities", "what do you know"))
        {
            var latest = sk.Latest();
            return latest == null ? V(v, "I CAN HOP! THAT'S IT", "HOP. FOR NOW", "HOP HOP HOP!") : $"{sk.Count} TRICKS! NEWEST: {sk.NameOf(latest)}";
        }
        if (sk && Has(m, "what's next", "whats next", "next trick", "next skill", "what will you learn", "new trick"))
        {
            var next = sk.Next();
            return next == null ? V(v, "I KNOW EVERY TRICK!", "I'M A PRO. OBVIOUSLY", "FULL ROAST. ALL TRICKS") : $"{sk.NameOf(next)} AT LV {next.level}!";
        }

        // its own level
        if (Has(m, "level", "lvl", "xp", "stats", "how big", "how old"))
            return progress ? $"LV {progress.Level} ({progress.Xp}/{progress.XpForNext} XP)" : "LV ???";
        return null;
    }

    // ------------------------------------------------------------------ the others

    static string AboutFriends(string m, BuddySpeech.Voice v, BuddyProgress progress)
    {
        bool apple = Has(m, "apple", "apples"), tomato = Has(m, "tomato", "tomatoes", "tomatos");
        bool bean = Has(m, "bean", "beans", "coffee bean"), burger = Has(m, "burger", "burgers", "hamburger", "cheeseburger");
        bool dino = Has(m, "dino", "dinos", "dinosaur", "trex", "t rex", "rex", "egg");
        if (!apple && !tomato && !bean && !burger && !dino) return null;
        var who = apple ? BuddySpeech.Voice.Apple : tomato ? BuddySpeech.Voice.Tomato : bean ? BuddySpeech.Voice.Bean : burger ? BuddySpeech.Voice.Burger : BuddySpeech.Voice.Dino;
        if (who == v) return Pick("THAT'S ME!", "ME!", "YES?");
        if (!Here(who))
        {
            if (v == BuddySpeech.Voice.Apple && progress)
            {
                if (who == BuddySpeech.Voice.Tomato && progress.Level < 5) return "I FEEL A SEED COMING";
                if (who == BuddySpeech.Voice.Bean && progress.Level < 10) return "MORE SEEDS AT LEVEL 10";
                if (who == BuddySpeech.Voice.Burger && progress.Level < 15) return "A BIG SEED AT LEVEL 15";
                if (who == BuddySpeech.Voice.Dino && progress.Level < 20) return "AN EGG? AT LEVEL 20...";
            }
            return Pick("WHO?", "A WHAT?");
        }
        if (v == BuddySpeech.Voice.Dino)
            return who == BuddySpeech.Voice.Apple ? Pick("APPLE FOUND MY EGG!", "APPLE IS MY MOM. OR DAD?") : who == BuddySpeech.Voice.Tomato ? Pick("TOMATO IS FAST! I CHASE", "TOMATO SAYS I'M LOUD")
                 : who == BuddySpeech.Voice.Bean ? Pick("BEAN SMELLS GOOD", "BEAN IS ZOOMY") : Pick("BURGER LOOKS... TASTY", "BIG BOUNCY FRIEND!");
        if (who == BuddySpeech.Voice.Dino)
            return v == BuddySpeech.Voice.Apple ? Pick("I HATCHED HIM!", "HE BITES. GENTLY") : v == BuddySpeech.Voice.Tomato ? Pick("DINO CHASED ME! FUN", "HE ROARS AT ME")
                 : v == BuddySpeech.Voice.Bean ? Pick("DINO IS LOUD. I LIKE IT", "RAWR-PRESSO!") : Pick("DINO EYES MY PATTY...", "LITTLE GUY, BIG TEETH");
        if (who == BuddySpeech.Voice.Burger)
            return v == BuddySpeech.Voice.Apple ? Pick("BURGER IS SO CHILL", "BEST HUGS EVER") : v == BuddySpeech.Voice.Tomato ? Pick("BURGER IS MY TRAMPOLINE", "HE'S SO BOUNCY") : Pick("BURGER NAPS TOO MUCH", "HE NEEDS A COFFEE");
        switch (v)
        {
            case BuddySpeech.Voice.Apple:
                return who == BuddySpeech.Voice.Tomato ? Pick("TOMATO IS MY BEST BUD", "HE'S SO SQUISHY") : Pick("BEAN NEVER SLEEPS", "HE'S SO JITTERY");
            case BuddySpeech.Voice.Tomato:
                return who == BuddySpeech.Voice.Apple ? Pick("APPLE IS OK I GUESS", "APPLE SNORES") : Pick("BEAN IS FAST. TOO FAST", "BEAN STOLE MY SNACK");
            case BuddySpeech.Voice.Burger:
                return who == BuddySpeech.Voice.Apple ? Pick("APPLE'S THE BOSS", "APPLE PLANTED ME") : who == BuddySpeech.Voice.Tomato ? Pick("TOMATO? MY TOPPING", "LITTLE GUY, BIG MOUTH") : Pick("BEAN NEEDS DECAF", "BEAN NEVER SITS");
            default:
                return who == BuddySpeech.Voice.Apple ? Pick("APPLE! THE BOSS", "APPLE IS SO CALM") : Pick("TOMATO? CHAOS", "TOMATO OWES ME A COOKIE");
        }
    }

    static bool Here(BuddySpeech.Voice who)
    {
        foreach (var g in GummyBody.All)
        {
            var sp = g ? g.GetComponent<BuddySpeech>() : null;
            if (sp && sp.voice == who) return true;
        }
        return false;
    }

    // the name of another buddy that's here (or null)
    static string SomeFriend(GummyBody me)
    {
        var names = new List<string>();
        foreach (var g in GummyBody.All)
        {
            var sp = g && g != me ? g.GetComponent<BuddySpeech>() : null;
            if (sp) names.Add(sp.Name);
        }
        return names.Count > 0 ? names[UnityEngine.Random.Range(0, names.Count)] : null;
    }

    static string Mood(GummyBrain brain, BuddySpeech.Voice v)
    {
        if (!brain) return "GOOD!";
        if (brain.Current == GummyBrain.State.Asleep) return "ZZZ... WHA?";
        switch (brain.Mood)
        {
            case CuteFace.Expression.Dizzy: return "@_@ SPINNY";
            case CuteFace.Expression.Grumpy: return brain.Annoyed ? "POKED TOO MUCH" : "HMPH";
            case CuteFace.Expression.Strain: return "HNNNG";
            case CuteFace.Expression.Happy: return "GREAT! ♥";
        }
        if (v == BuddySpeech.Voice.Tomato) return Pick("GOOD. U?", "BORED");
        if (v == BuddySpeech.Voice.Bean) return Pick("WIRED! U?", "BUZZING!");
        if (v == BuddySpeech.Voice.Burger) return Pick("CHILL. U?", "A BIT HUNGRY", "WELL DONE, THANKS");
        if (v == BuddySpeech.Voice.Dino) return Pick("FIERCE! U?", "HUNGRY. RAWR", "SO GOOD! RAWR!");
        return Pick("GOOD! U?", "JUICY!");
    }

    // ------------------------------------------------------------------ text helpers

    /// <summary>Lower-case, punctuation turned into spaces (keeps ? ' &lt; 3 and the heart), single spaces.</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (char ch in s.ToLowerInvariant())
        {
            char c = char.IsLetterOrDigit(ch) || ch == '\'' || ch == '<' || ch == '♥' ? ch : ' ';
            if (c == ' ') { if (!space && sb.Length > 0) sb.Append(' '); space = true; }
            else { sb.Append(c); space = false; }
        }
        return sb.ToString().TrimEnd();
    }

    // whole words / phrases only ("hi" doesn't match "this")
    static bool Has(string m, params string[] words)
    {
        foreach (string w in words) if (m.Contains(" " + w + " ")) return true;
        return false;
    }

    // up to maxWords words after the first of these phrases (stops at "and", "but", "too"...), or null
    static string Phrase(string m, int maxWords, params string[] starts)
    {
        foreach (string s in starts)
        {
            int i = m.IndexOf(" " + s + " ", StringComparison.Ordinal);
            if (i < 0) continue;
            string rest = m.Substring(i + s.Length + 2).Trim();
            if (rest.Length == 0) return null;
            var words = new List<string>();
            foreach (string w in rest.Split(' '))
            {
                if (w.Length == 0) continue;
                if (Array.IndexOf(Stops, w) >= 0 || words.Count >= maxWords) break;
                words.Add(w);
            }
            return words.Count > 0 ? string.Join(" ", words.ToArray()) : null;
        }
        return null;
    }

    // the single word after one of these phrases
    static string Word(string m, params string[] starts) => Phrase(m, 1, starts);

    // what's between two markers ("my favorite <food> is"), at most two words
    static string Between(string m, string after, params string[] before)
    {
        int i = m.IndexOf(after, StringComparison.Ordinal);
        if (i < 0) return null;
        int from = i + after.Length;
        foreach (string b in before)
        {
            int j = m.IndexOf(b, from - 1, StringComparison.Ordinal);
            if (j < 0) continue;
            string w = m.Substring(from, Mathf.Max(0, j - from)).Trim();
            if (w.Length == 0 || w.Split(' ').Length > 2) return null;
            return w;
        }
        return null;
    }

    static bool Starts(string phrase, params string[] words)
    {
        foreach (string w in words) if (phrase == w || phrase.StartsWith(w + " ")) return true;
        return false;
    }

    // "I'm Gašper" (a capital letter after I'm / I am, so "i'm tired" isn't a name)
    static string CapitalAfterIAm(string raw)
    {
        string[] t = raw.Replace(",", " ").Replace("!", " ").Replace(".", " ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < t.Length; i++)
        {
            string a = t[i].ToLowerInvariant().Replace('’', '\'');
            int next = i + 1;
            if (a == "i" && t[next].ToLowerInvariant() == "am" && next + 1 < t.Length) next++;
            else if (a != "i'm" && a != "im") continue;
            string w = t[next];
            if (w.Length >= 2 && char.IsUpper(w[0]) && !(w.Length > 1 && char.IsUpper(w[1]))) return w.ToLowerInvariant();
        }
        return null;
    }

    static bool IsName(string w)
    {
        if (w.Length < 2 || w.Length > 16 || Array.IndexOf(NotNames, w) >= 0) return false;
        foreach (char c in w) if (!char.IsLetter(c) && c != '-' && c != '\'') return false;
        return true;
    }

    // the word as you typed it (capitals, accents)
    static string Original(string raw, string lower)
    {
        foreach (string t in raw.Split(new[] { ' ', ',', '.', '!', '?', ':', ';' }, StringSplitOptions.RemoveEmptyEntries))
            if (t.ToLowerInvariant() == lower) return t;
        return char.ToUpperInvariant(lower[0]) + lower.Substring(1);
    }

    static string LastWord(string s)
    {
        string n = Normalize(s);
        int i = n.LastIndexOf(' ');
        return i >= 0 ? n.Substring(i + 1) : n;
    }

    // a fused monster (BuddyMonster): goofy-scary, "WE"
    static string MonsterReply(string m, BuddySpeech speech)
    {
        string name = speech ? speech.Name : "MONSTER";
        if (Has(m, "who are you", "your name", "what are you", "name")) return Pick("WE ARE " + name + "! RAAAWR", name + ". FEAR US");
        if (Has(m, "split", "apart", "come out", "let them go", "separate", "unfuse", "give them back")) return Pick("PULL US APART. IF YOU DARE", "NEVER! ...MAYBE IF YOU PULL");
        if (Has(m, "hi", "hello", "hey", "yo")) return Pick("RAAAWR. HELLO, HUMAN", "HELLO TINY HUMAN");
        if (Has(m, "snack", "food", "hungry", "cookie", "candy", "eat")) return Pick("SNACKS! FEED US!", "ONE BITE. ALL OF IT");
        if (Has(m, "cute", "love", "good", "nice", "pet")) return Pick("WE ARE NOT CUTE. ...A LITTLE", "RAWR \u2665");
        if (Has(m, "scary", "monster", "scare", "afraid")) return Pick("YES. VERY SCARY. RAAAWR", "BOO!");
        return Pick("RAAAWR!", "WE DON'T UNDERSTAND. WE ARE A MONSTER", "GRRR... WHAT?", "WE ARE MANY. WE HEAR YOU", "FEED US AND WE'LL LISTEN");
    }

    // bullet time: stop it, and (only while it's stopped) let it go again
    static readonly string[] FreezeWords = { "freeze", "freeze time", "time freeze", "stop time", "stop the time", "time stop", "pause time", "pause the world", "freeze frame", "bullet time", "slow motion", "slow mo", "slowmo", "everybody freeze", "everyone freeze", "freeze everyone", "freeze everything", "stop everything" };
    static readonly string[] GoWords = { "go", "go go", "go on", "lets go", "let's go", "resume", "unfreeze", "un freeze", "unpause", "un pause", "play", "action", "and action", "continue", "start time", "time go", "time resume", "let it go", "move", "you can move", "release", "carry on", "time flows" };
    static readonly string[] JamWords = { "jam", "jam session", "music", "play music", "play some music", "band", "concert", "song", "play a song", "sing", "sing a song", "sing for me", "rock out", "make music", "dance party" };

    static string V(BuddySpeech.Voice v, string apple, string tomato, string bean, string burger = null, string dino = null) =>
        v == BuddySpeech.Voice.Tomato ? tomato : v == BuddySpeech.Voice.Bean ? bean : v == BuddySpeech.Voice.Burger && burger != null ? burger
        : v == BuddySpeech.Voice.Dino && dino != null ? dino : apple;

    static string Pick(params string[] s) => s[UnityEngine.Random.Range(0, s.Length)];
}
