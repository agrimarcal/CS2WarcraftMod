﻿using System;
using System.Collections.Generic;
using System.Linq;
using WarcraftPlugin.Cooldowns;
using WarcraftPlugin.Effects;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using WarcraftPlugin.Helpers;
using System.Text.RegularExpressions;
using WarcraftPlugin.Resources;
using CounterStrikeSharp.API.Modules.Admin;
using WarcraftPlugin.Adverts;
using CounterStrikeSharp.API.Modules.Cvars;
using System.Text.Json.Serialization;
using WarcraftPlugin.Events;
using WarcraftPlugin.Classes;
using WarcraftPlugin.Menu;
using WarcraftPlugin.Menu.WarcraftMenu;
using WarcraftPlugin.Core;

namespace WarcraftPlugin
{
    public class Config : BasePluginConfig
    {
        [JsonPropertyName("ConfigVersion")] public override int Version { get; set; } = 2;

        [JsonPropertyName("DeactivatedClasses")] public string[] DeactivatedClasses { get; set; } = [];
        [JsonPropertyName("ShowCommandAdverts")] public bool ShowCommandAdverts { get; set; } = false;
        [JsonPropertyName("NecromancerUseZombieModel")] public bool NecromancerUseZombieModel { get; set; } = true;
    }

    public static class WarcraftPlayerExtensions
    {
        public static WarcraftPlayer GetWarcraftPlayer(this CCSPlayerController player)
        {
            return WarcraftPlugin.Instance.GetWcPlayer(player);
        }
    }

    public class WarcraftPlugin : BasePlugin, IPluginConfig<Config>
    {
        private static WarcraftPlugin _instance;
        public static WarcraftPlugin Instance => _instance;

        public override string ModuleName => "Warcraft";
        public override string ModuleVersion => "2.0.0";

        public const int MaxLevel = 16;
        public const int MaxSkillLevel = 5;
        public const int maxUltimateLevel = 1;

        private readonly Dictionary<IntPtr, WarcraftPlayer> WarcraftPlayers = [];
        private EventSystem _eventSystem;
        public XpSystem XpSystem;
        public ClassManager classManager;
        public EffectManager EffectManager;
        public CooldownManager CooldownManager;
        public AdvertManager AdvertManager;
        private Database _database;

        public int XpPerKill = 40;
        public float XpHeadshotModifier = 0.15f;
        public float XpKnifeModifier = 0.25f;

        public int BotQuota = 10;

        public List<WarcraftPlayer> Players => WarcraftPlayers.Values.ToList();

        public Config Config { get; set; } = null!;

        public WarcraftPlayer GetWcPlayer(CCSPlayerController player)
        {
            WarcraftPlayers.TryGetValue(player.Handle, out var wcPlayer);
            if (wcPlayer == null)
            {
                if (player.IsValid && !player.IsBot)
                {
                    WarcraftPlayers[player.Handle] = _database.LoadPlayerFromDatabase(player, XpSystem);
                }
                else
                {
                    return null;
                }
            }

            return WarcraftPlayers[player.Handle];
        }

        public void SetWcPlayer(CCSPlayerController player, WarcraftPlayer wcPlayer)
        {
            WarcraftPlayers[player.Handle] = wcPlayer;
        }

        public static void RefreshPlayerName(WarcraftPlayer wcPlayer)
        {
            if (wcPlayer == null || !wcPlayer.Player.IsValid) return;

            var playerNameClean = Regex.Replace(wcPlayer.Player.PlayerName, @"\d+ \[\w+\] ", "");
            wcPlayer.Player.PlayerName = $"{wcPlayer.currentLevel} [{wcPlayer.GetClass().DisplayName}] {playerNameClean}";
            wcPlayer.Player.Clan = "";
            Utilities.SetStateChanged(wcPlayer.Player, "CBasePlayerController", "m_iszPlayerName");
            Utilities.SetStateChanged(wcPlayer.Player, "CCSPlayerController", "m_szClan");
        }

        public override void Load(bool hotReload)
        {
            base.Load(hotReload);
            MenuAPI.Load(this, hotReload);

            _instance ??= this;

            XpSystem = new XpSystem(this);
            XpSystem.GenerateXpCurve(110, 1.07f, MaxLevel);

            _database = new Database();
            classManager = new ClassManager();
            classManager.Initialize();

            EffectManager = new EffectManager();
            EffectManager.Initialize();

            CooldownManager = new CooldownManager();
            CooldownManager.Initialize();

            if (Config.ShowCommandAdverts)
            {
                AdvertManager = new AdvertManager();
                AdvertManager.Initialize();
            }

            AddCommand("ultimate", "ultimate", UltimatePressed);

            AddCommand("changerace", "change class", (player, _) => ShowClassMenu(player));
            AddCommand("changeclass", "change class", (player, _) => ShowClassMenu(player));
            AddCommand("race", "change class", (player, _) => ShowClassMenu(player));
            AddCommand("class", "change class", (player, _) => ShowClassMenu(player));
            AddCommand("rpg", "change class", (player, _) => ShowClassMenu(player));
            AddCommand("cr", "change class", (player, _) => ShowClassMenu(player));

            AddCommand("reset", "reset skills", CommandResetSkills);
            AddCommand("factoryreset", "reset levels", CommandFactoryReset);

            AddCommand("addxp", "addxp", CommandAddXp);

            AddCommand("skills", "skills", (player, _) => ShowSkillsMenu(player));
            AddCommand("level", "skills", (player, _) => ShowSkillsMenu(player));

            AddCommand("rpg_help", "list all commands", CommandHelp);
            AddCommand("commands", "list all commands", CommandHelp);
            AddCommand("wcs", "list all commands", CommandHelp);
            AddCommand("war3menu", "list all commands", CommandHelp);

            RegisterListener<Listeners.OnClientConnect>(OnClientPutInServerHandler);
            RegisterListener<Listeners.OnMapStart>(OnMapStartHandler);
            RegisterListener<Listeners.OnMapEnd>(OnMapEndHandler);
            RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnectHandler);

            RegisterListener<Listeners.OnServerPrecacheResources>((manifest) =>
            {
                //Characters
                manifest.AddResource("characters/models/nozb1/skeletons_player_model/skeleton_player_model_2/skeleton_nozb2_pm.vmdl");

                //Models
                manifest.AddResource("models/weapons/w_eq_beartrap_dropped.vmdl");
                manifest.AddResource("models/props/de_dust/hr_dust/dust_crates/dust_crate_style_01_32x32x32.vmdl");
                manifest.AddResource("models/tools/bullet_hit_marker.vmdl");
                manifest.AddResource("models/generic/bust_02/bust_02_a.vmdl"); //destructable prop
                manifest.AddResource("models/weapons/w_muzzlefireshape.vmdl"); //fireball
                manifest.AddResource("models/weapons/w_eq_bumpmine.vmdl"); //drone
                manifest.AddResource("models/anubis/structures/pillar02_base01.vmdl"); //spring trap

                //manifest.AddResource("models/weapons/w_eq_tablet_dropped.vmdl");
                //manifest.AddResource("models/weapons/w_eq_tablet.vmdl");
                //manifest.AddResource("models/generic/conveyor_control_panel_01/conveyor_control_screen_01.vmdl");
                //"models/props/crates/csgo_drop_crate_community_22.vmdl", shop???
                //sounds/ui/panorama/claim_gift_01.vsnd_c // shop sound??
                //sounds/physics/metal/playertag_pickup_01.vsnd_c //shop sound
                manifest.AddResource("sounds/physics/body/body_medium_break3.vsnd");

                //sounds/music/survival_review_victory.vsnd_c // cool track

                foreach (var prop in Shapeshifter.Props)
                {
                    manifest.AddResource(prop);
                }

                foreach (var p in Particles.Paths)
                {
                    manifest.AddResource(p);
                }

            });

            if (hotReload)
            {
                OnMapStartHandler(null);
            }

            _eventSystem = new EventSystem(this, Config);
            _eventSystem.Initialize();

            _database.Initialize(ModuleDirectory);
        }

        private void ShowSkillsMenu(CCSPlayerController player)
        {
            SkillsMenu.Show(GetWcPlayer(player));
        }

        private void ShowClassMenu(CCSPlayerController player)
        {
            var databaseClassInformation = _database.LoadClassInformationFromDatabase(player);
            ClassMenu.Show(player, databaseClassInformation);
        }

        [RequiresPermissions("@css/addxp")]
        private void CommandAddXp(CCSPlayerController? client, CommandInfo commandinfo)
        {
            if (string.IsNullOrEmpty(commandinfo.ArgByIndex(1))) return;

            var xpToAdd = Convert.ToInt32(commandinfo.ArgByIndex(1));

            Console.WriteLine($"Adding {xpToAdd} xp to player {client.PlayerName}");
            XpSystem.AddXp(client, xpToAdd);
        }

        private void CommandHelp(CCSPlayerController? player, CommandInfo commandinfo)
        {
            player.PrintToChat($" {ChatColors.Green}Type !class to change classes, !skills to level-up");
        }

        private void CommandResetSkills(CCSPlayerController? client, CommandInfo commandinfo)
        {
            var wcPlayer = GetWcPlayer(client);

            for (int i = 0; i < 4; i++)
            {
                wcPlayer.SetAbilityLevel(i, 0);
            }

            if (XpSystem.GetFreeSkillPoints(wcPlayer) > 0)
            {
                SkillsMenu.Show(wcPlayer);
            }
        }

        private void CommandFactoryReset(CCSPlayerController client, CommandInfo commandInfo)
        {
            var wcPlayer = GetWcPlayer(client);
            wcPlayer.currentLevel = 1;
            wcPlayer.currentXp = 0;
            CommandResetSkills(client, commandInfo);
            client.PlayerPawn.Value.CommitSuicide(false, false);
        }

        private void OnClientDisconnectHandler(int slot)
        {
            var player = new CCSPlayerController(NativeAPI.GetEntityFromIndex(slot + 1));
            // No bots, invalid clients or non-existent clients.
            if (!player.IsValid || player.IsBot) return;

            SetWcPlayer(player, null);
            _database.SavePlayerToDatabase(player);
        }

        private void OnMapStartHandler(string mapName)
        {
            AddTimer(60f, StatusUpdate, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
            AddTimer(60.0f, _database.SaveClients, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);

            BotQuota = ConVar.Find("bot_quota").GetPrimitiveValue<int>();
            Server.PrintToConsole("Map Load Warcraft\n");
        }

        private void OnMapEndHandler()
        {
            _database.SaveClients();
        }

        private void StatusUpdate()
        {
            var playerEntities = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller");
            foreach (var player in playerEntities)
            {
                if (!player.IsValid) continue;

                var wcPlayer = GetWcPlayer(player);

                if (wcPlayer == null) continue;

                RefreshPlayerName(wcPlayer);
            }
        }

        private void OnClientPutInServerHandler(int slot, string name, string ipAddress)
        {
            var player = new CCSPlayerController(NativeAPI.GetEntityFromIndex(slot + 1));
            Console.WriteLine($"Put in server {player.Handle}");
            // No bots, invalid clients or non-existent clients.
            if (!player.IsValid || player.IsBot) return;

            if (!_database.PlayerExistsInDatabase(player.SteamID))
            {
                _database.AddNewPlayerToDatabase(player);
            }

            WarcraftPlayers[player.Handle] = _database.LoadPlayerFromDatabase(player, XpSystem);

            Console.WriteLine("Player just connected: " + WarcraftPlayers[player.Handle]);
        }

        public void ChangeClass(CCSPlayerController player, string classInternalName)
        {
            _database.SavePlayerToDatabase(player);

            // Dont do anything if were already that race.
            if (classInternalName == player.GetWarcraftPlayer().className) return;

            player.GetWarcraftPlayer().GetClass().PlayerChangingToAnotherRace();
            player.GetWarcraftPlayer().className = classInternalName;

            _database.SaveCurrentClass(player);
            _database.LoadPlayerFromDatabase(player, XpSystem);
            if (player.PawnIsAlive)
            {
                player.PlayerPawn.Value.CommitSuicide(false, false);
            }
            RefreshPlayerName(player.GetWarcraftPlayer());
        }

        private void UltimatePressed(CCSPlayerController? client, CommandInfo commandinfo)
        {
            var warcraftPlayer = client.GetWarcraftPlayer();
            if (warcraftPlayer.GetAbilityLevel(3) < 1)
            {
                client.PrintToCenter("No levels in ultimate");
                client.PlayLocalSound("sounds/ui/menu_invalid.vsnd");
            }
            else if (!warcraftPlayer.GetClass().IsAbilityReady(3))
            {
                client.PrintToCenter($"Ultimate ready in {Math.Ceiling(warcraftPlayer.GetClass().AbilityCooldownRemaining(3))}s");
                client.PlayLocalSound("sounds/ui/menu_invalid.vsnd");
            }
            else
            {
                GetWcPlayer(client)?.GetClass()?.InvokeAbility(3);
            }
        }

        public override void Unload(bool hotReload)
        {
            base.Unload(hotReload);
        }

        public void OnConfigParsed(Config config)
        {
            Config = config;
        }
    }
}