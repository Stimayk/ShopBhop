using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ShopAPI;
using System.Globalization;

namespace ShopBhop
{
    public class ShopBhop : BasePlugin
    {
        public override string ModuleName => "[SHOP] BHOP";
        public override string ModuleDescription => "";
        public override string ModuleAuthor => "E!N";
        public override string ModuleVersion => "v1.0.0";

        private IShopApi? SHOP_API;
        private const string CategoryName = "Bhop";
        public static JObject? JsonBhop { get; private set; }
        private readonly PlayerBhop[] playerBhop = new PlayerBhop[65];
        private readonly BhopSettings[] _isBhopActive = new BhopSettings[70];

        public override void OnAllPluginsLoaded(bool hotReload)
        {
            SHOP_API = IShopApi.Capability.Get();
            if (SHOP_API == null) return;

            LoadConfig();
            InitializeShopItems();
            SetupTimersAndListeners();
        }

        private void LoadConfig()
        {
            string configPath = Path.Combine(ModuleDirectory, "../../configs/plugins/Shop/Bhop.json");
            if (File.Exists(configPath))
            {
                JsonBhop = JObject.Parse(File.ReadAllText(configPath));
            }
        }

        private void InitializeShopItems()
        {
            if (JsonBhop == null || SHOP_API == null) return;

            SHOP_API.CreateCategory(CategoryName, "Αυξο");

            foreach (var item in JsonBhop.Properties().Where(p => p.Value is JObject))
            {
                AddShopItemAsync(item).Wait();
            }
        }

        private async Task AddShopItemAsync(JProperty item)
        {
            int itemId = await SHOP_API!.AddItem(
                item.Name,
                (string)item.Value["name"]!,
                CategoryName,
                (int)item.Value["price"]!,
                (int)item.Value["sellprice"]!,
                (int)item.Value["duration"]!
            );
            SHOP_API.SetItemCallbacks(itemId, OnClientBuyItem, OnClientSellItem, OnClientToggleItem);
        }

        private void SetupTimersAndListeners()
        {
            RegisterListener<Listeners.OnClientDisconnect>(playerSlot => playerBhop[playerSlot] = null!);
            RegisterListener<Listeners.OnTick>(() =>
            {
                foreach (var player in Utilities.GetPlayers().Where(player => player is { IsValid: true, IsBot: false, PawnIsAlive: true }))
                {
                    if (playerBhop[player.Slot] != null)
                    {
                        OnTick(player);
                    }
                }
            });

            RegisterEventHandler<EventRoundStart>(EventRoundStart);

            for (int i = 0; i < _isBhopActive.Length; i++)
            {
                _isBhopActive[i] = new BhopSettings();
            }
        }

        public void OnClientBuyItem(CCSPlayerController player, int itemId, string categoryName, string uniqueName, int buyPrice, int sellPrice, int duration, int count)
        {
            if (TryGetItemAttributes(uniqueName, out var attributes))
            {
                playerBhop[player.Slot] = new PlayerBhop(attributes.Jumps, attributes.Cooldown, attributes.Timer, attributes.MaxSpeed, itemId);
            }
        }

        public void OnClientToggleItem(CCSPlayerController player, int itemId, string uniqueName, int state)
        {
            if (state == 1 && TryGetItemAttributes(uniqueName, out var attributes))
            {
                playerBhop[player.Slot] = new PlayerBhop(attributes.Jumps, attributes.Cooldown, attributes.Timer, attributes.MaxSpeed, itemId);
                player.PrintToChat(Localizer["BhopTurnOnNextRound"]);
            }
            else if (state == 0)
            {
                OnClientSellItem(player, itemId, uniqueName, 0);
            }
        }

        public void OnClientSellItem(CCSPlayerController player, int itemId, string uniqueName, int sellPrice)
        {
            playerBhop[player.Slot] = null!;
        }

        private static bool TryGetItemAttributes(string uniqueName, out (int Jumps, int MaxSpeed, float Timer, float Cooldown) attributes)
        {
            attributes = default;
            if (JsonBhop != null && JsonBhop.TryGetValue(uniqueName, out var obj) && obj is JObject jsonItem)
            {
                attributes.Jumps = (int)jsonItem["jumps"]!;
                attributes.MaxSpeed = (int)jsonItem["maxspeed"]!;
                attributes.Timer = float.Parse(jsonItem["timer"]!.ToString(), CultureInfo.InvariantCulture);
                attributes.Cooldown = float.Parse(jsonItem["cooldown"]!.ToString(), CultureInfo.InvariantCulture);
                return true;
            }
            return false;
        }

        private void OnTick(CCSPlayerController player)
        {
            var playerBhopItem = playerBhop[player.Slot];
            if (playerBhopItem == null) return;

            if (ConVar.Find("sv_autobunnyhopping")!.GetPrimitiveValue<bool>()) return;

            var playerPawn = player.PlayerPawn.Value;
            if (playerPawn != null && _isBhopActive[player.Slot].Active)
            {
                ApplyBhopMechanics(player, playerPawn, playerBhopItem);
            }
        }

        private void ApplyBhopMechanics(CCSPlayerController player, CCSPlayerPawn playerPawn, PlayerBhop playerBhopItem)
        {
            var maxSpeed = playerBhopItem.MaxSpeed;
            if (Math.Round(playerPawn.AbsVelocity.Length2D()) > maxSpeed && maxSpeed != 0)
            {
                ChangeVelocity(playerPawn, maxSpeed);
            }

            var flags = (PlayerFlags)playerPawn.Flags;
            var buttons = player.Buttons;

            if (buttons.HasFlag(PlayerButtons.Jump) && flags.HasFlag(PlayerFlags.FL_ONGROUND) &&
                !playerPawn.MoveType.HasFlag(MoveType_t.MOVETYPE_LADDER) && playerBhopItem.Jumps > 0)
            {
                playerPawn.AbsVelocity.Z = 300;
                playerBhopItem.Jumps--;

                if (playerBhopItem.Jumps == 0)
                {
                    player.PrintToChat(Localizer["Cooldown", playerBhopItem.Cooldown]);
                    _isBhopActive[player.Slot].Active = false;
                    AddTimer(playerBhopItem.Cooldown, () =>
                    {
                        playerBhopItem.Jumps = playerBhopItem.MaxJumps;
                        _isBhopActive[player.Slot].Active = true;
                        player.PrintToChat(Localizer["MaxJumps", playerBhopItem.MaxJumps]);
                    }, TimerFlags.STOP_ON_MAPCHANGE);
                }
            }
        }

        private HookResult EventRoundStart(EventRoundStart @event, GameEventInfo info)
        {
            if (Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First().GameRules!.WarmupPeriod)
                return HookResult.Continue;

            var gamerules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First().GameRules;
            if (gamerules == null) return HookResult.Continue;

            foreach (var player in Utilities.GetPlayers().Where(player => player is { IsValid: true, IsBot: false, PawnIsAlive: true }))
            {
                ResetPlayerBhopStateForNewRound(player, gamerules.FreezeTime);
            }

            return HookResult.Continue;
        }

        private void ResetPlayerBhopStateForNewRound(CCSPlayerController player, float freezeTime)
        {
            _isBhopActive[player.Slot].Active = false;

            if (playerBhop[player.Slot] != null)
            {
                var playerBhopItem = playerBhop[player.Slot];
                _isBhopActive[player.Slot].MaxSpeed = playerBhopItem.MaxSpeed;

                if (playerBhopItem.Timer != 0)
                {
                    player.PrintToChat(Localizer["TimeToActivation", playerBhopItem.Timer + freezeTime]);
                }
                AddTimer(playerBhopItem.Timer + freezeTime, () =>
                {
                    player.PrintToChat(Localizer["MaxJumps", playerBhopItem.MaxJumps]);
                    _isBhopActive[player.Slot].Active = true;
                }, TimerFlags.STOP_ON_MAPCHANGE);
            }
        }

        private static void ChangeVelocity(CCSPlayerPawn? pawn, float vel)
        {
            if (pawn == null) return;

            var currentVelocity = new Vector(pawn.AbsVelocity.X, pawn.AbsVelocity.Y, pawn.AbsVelocity.Z);
            var currentSpeed3D = Math.Sqrt(currentVelocity.X * currentVelocity.X + currentVelocity.Y * currentVelocity.Y + currentVelocity.Z * currentVelocity.Z);

            pawn.AbsVelocity.X = (float)(currentVelocity.X / currentSpeed3D) * vel;
            pawn.AbsVelocity.Y = (float)(currentVelocity.Y / currentSpeed3D) * vel;
            pawn.AbsVelocity.Z = (float)(currentVelocity.Z / currentSpeed3D) * vel;
        }

        public record PlayerBhop(int MaxJumps, float Cooldown, float Timer, int MaxSpeed, int ItemID)
        {
            public int Jumps { get; set; } = MaxJumps;
        }
    }
    public class BhopSettings
    {
        [JsonIgnore] public bool Active { get; set; }
        public float Timer { get; set; }
        public float MaxSpeed { get; set; }
    }
}
