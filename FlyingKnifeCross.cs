using System;
using System.Collections.Concurrent;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace FlyingKnifeCross
{
    [ApiVersion(2, 1)]
    public class FlyingKnifeCrossPlugin : TerrariaPlugin
    {
        public override string Name => "FlyingKnifeCross";
        public override string Author => "You";
        public override string Description =>
            "Every N Flying Knife hits, 4 Shadowflame Knives slash through the enemy in a cross.";
        public override Version Version => new Version(1, 0, 0);

        // ===================== SETTINGS (tweak these) =====================
        private const int HitsRequired = 10;       // hits needed to trigger the cross
        private const float SpawnDistance = 96f;   // pixels from enemy center (16 px = 1 tile)
        private const float KnifeSpeed = 24f;      // pixels per tick, higher = faster slash
        private const int KnifeDamage = 150;       // damage of each knife
        private const float KnifeKnockback = 2f;
        private const int KnifeLifetime = 14;      // ticks before the knife disappears
        // 255 = server-owned, keeps unlimited piercing working.
        // Set to a player index if you want damage credited to the player instead
        // (note: piercing may then be reset by that player's client).
        private const int ProjectileOwner = 255;
        // ==================================================================

        private readonly int[] _hits = new int[256];
        private readonly ConcurrentQueue<int> _pending = new ConcurrentQueue<int>();

        public FlyingKnifeCrossPlugin(Main game) : base(game) { }

        public override void Initialize()
        {
            GetDataHandlers.NPCStrike += OnNpcStrike;
            ServerApi.Hooks.GameUpdate.Register(this, OnUpdate);
            ServerApi.Hooks.ServerLeave.Register(this, OnLeave);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                GetDataHandlers.NPCStrike -= OnNpcStrike;
                ServerApi.Hooks.GameUpdate.Deregister(this, OnUpdate);
                ServerApi.Hooks.ServerLeave.Deregister(this, OnLeave);
            }
            base.Dispose(disposing);
        }

        private void OnLeave(LeaveEventArgs args)
        {
            if (args.Who >= 0 && args.Who < _hits.Length)
                _hits[args.Who] = 0;
        }

        private void OnNpcStrike(object sender, GetDataHandlers.NPCStrikeEventArgs e)
        {
            if (e.Handled)
                return;

            TSPlayer plr = e.Player;
            if (plr == null || !plr.Active)
                return;

            if (plr.TPlayer.HeldItem.type != ItemID.FlyingKnife)
                return;

            if (e.ID < 0 || e.ID >= Main.maxNPCs)
                return;

            NPC npc = Main.npc[e.ID];
            if (!npc.active || npc.friendly || npc.life <= 0)
                return;

            if (++_hits[plr.Index] < HitsRequired)
                return;

            _hits[plr.Index] = 0;
            _pending.Enqueue(e.ID);
        }

        private void OnUpdate(EventArgs args)
        {
            while (_pending.TryDequeue(out int id))
            {
                NPC npc = Main.npc[id];

                // Only trigger on enemies that survived the hit.
                if (!npc.active || npc.life <= 0)
                    continue;

                SpawnCross(npc.Center);
            }
        }

        private void SpawnCross(Vector2 center)
        {
            Vector2[] directions =
            {
                new Vector2(0f, -1f), // from above
                new Vector2(0f, 1f),  // from below
                new Vector2(-1f, 0f), // from the left
                new Vector2(1f, 0f)   // from the right
            };

            foreach (Vector2 dir in directions)
            {
                Vector2 position = center + dir * SpawnDistance;
                Vector2 velocity = -dir * KnifeSpeed; // aimed at the enemy

                int index = Projectile.NewProjectile(
                    new EntitySource_Misc("FlyingKnifeCross"),
                    position.X, position.Y,
                    velocity.X, velocity.Y,
                    ProjectileID.ShadowFlameKnife,
                    KnifeDamage, KnifeKnockback,
                    ProjectileOwner);

                if (index < 0 || index >= Main.maxProjectiles)
                    continue;

                Projectile proj = Main.projectile[index];
                proj.friendly = true;
                proj.hostile = false;
                proj.penetrate = -1;      // unlimited piercing
                proj.tileCollide = false; // slash through walls too
                proj.timeLeft = KnifeLifetime;

                NetMessage.SendData((int)PacketTypes.ProjectileNew, -1, -1, null, index);
            }
        }
    }
}
