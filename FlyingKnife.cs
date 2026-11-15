using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace FlyingKnifeBuffPlugin
{
    [ApiVersion(2, 1)]
    public class FlyingKnifeBuff : TerrariaPlugin
    {
        public override string Name => "FlyingKnifeBuff";
        public override System.Version Version => new System.Version(1, 0, 0);
        public override string Author => "CustomPlugin";
        public override string Description => "Buff Flying Knife: 2x attack speed (-15% damage) dan memanggil 4 pisau formasi kompas setiap 4x hit selama 1 detik.";

        private class CompassKnifeData
        {
            public int ProjIndex;
            public int TargetNpcIndex;
            public int OwnerIndex;
            public int DirectionIndex; // 0 = Utara, 1 = Timur, 2 = Selatan, 3 = Barat
            public int TicksLeft;
        }

        private int[] hitCounter = new int[Main.maxPlayers];
        private int[] extraAttackCooldown = new int[Main.maxPlayers];
        private List<CompassKnifeData> activeCompassKnives = new List<CompassKnifeData>();

        public FlyingKnifeBuff(Main game) : base(game) { }

        public override void Initialize()
        {
            ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);
            ServerApi.Hooks.NpcStrike.Register(this, OnNpcStrike);
            ServerApi.Hooks.ServerJoin.Register(this, OnServerJoin);
            ServerApi.Hooks.ServerLeave.Register(this, OnServerLeave);

            Commands.ChatCommands.Add(new Command("flyingknife.buff", CheckStatusCommand, "flyingknife", "fkbuff")
            {
                HelpText = "Mengecek status buff senjata Flying Knife."
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.GameUpdate.Deregister(this, OnGameUpdate);
                ServerApi.Hooks.NpcStrike.Deregister(this, OnNpcStrike);
                ServerApi.Hooks.ServerJoin.Deregister(this, OnServerJoin);
                ServerApi.Hooks.ServerLeave.Deregister(this, OnServerLeave);
            }
            base.Dispose(disposing);
        }

        private void CheckStatusCommand(CommandArgs args)
        {
            args.Player.SendSuccessMessage("[FlyingKnifeBuff] Aktif! Attack speed 2x (-15% damage) & setiap 4x hit memanggil 4 pisau kompas selama 1 detik.");
        }

        private void OnServerJoin(JoinEventArgs args)
        {
            if (args.Who >= 0 && args.Who < Main.maxPlayers)
            {
                hitCounter[args.Who] = 0;
                extraAttackCooldown[args.Who] = 0;
            }
        }

        private void OnServerLeave(LeaveEventArgs args)
        {
            if (args.Who >= 0 && args.Who < Main.maxPlayers)
            {
                hitCounter[args.Who] = 0;
                extraAttackCooldown[args.Who] = 0;
            }
        }

        private void OnNpcStrike(NpcStrikeEventArgs args)
        {
            if (args.Handled || args.Player == null || args.Npc == null || !args.Npc.active)
                return;

            int plrIdx = args.Player.whoAmI;
            if (plrIdx < 0 || plrIdx >= Main.maxPlayers)
                return;

            TSPlayer tsPlayer = TShock.Players[plrIdx];
            if (tsPlayer == null || !tsPlayer.Active || !tsPlayer.HasPermission("flyingknife.buff"))
                return;

            Item heldItem = args.Player.HeldItem;
            if (heldItem == null || heldItem.type != ItemID.FlyingKnife)
                return;

            // Terapkan nerf damage 15% (menjadi 85%)
            args.Damage = Math.Max(1, (int)(args.Damage * 0.85f));

            // Hitung jumlah hit untuk memicu formasi kompas setiap 4x hit
            hitCounter[plrIdx]++;
            if (hitCounter[plrIdx] >= 4)
            {
                hitCounter[plrIdx] = 0;
                int nerfedDamage = Math.Max(1, (int)(args.Player.GetWeaponDamage(heldItem) * 0.85f));
                SpawnCompassKnives(args.Player, args.Npc, nerfedDamage, heldItem.knockBack);
            }
        }

        private void SpawnCompassKnives(Player player, NPC targetNpc, int damage, float knockBack)
        {
            // 4 Arah Kompas: Utara (Atas), Timur (Kanan), Selatan (Bawah), Barat (Kiri)
            Vector2[] offsets = new Vector2[]
            {
                new Vector2(0f, -95f),  // 0: Utara
                new Vector2(95f, 0f),   // 1: Timur
                new Vector2(0f, 95f),   // 2: Selatan
                new Vector2(-95f, 0f)   // 3: Barat
            };

            for (int dir = 0; dir < 4; dir++)
            {
                Vector2 spawnPos = targetNpc.Center + offsets[dir];
                Vector2 toTarget = targetNpc.Center - spawnPos;
                toTarget.Normalize();
                Vector2 velocity = toTarget * 22f;

                int projIdx = Projectile.NewProjectile(
                    player.GetProjectileSource_Item(player.HeldItem),
                    spawnPos,
                    velocity,
                    ProjectileID.FlyingKnife,
                    damage,
                    knockBack * 0.5f,
                    player.whoAmI
                );

                if (projIdx >= 0 && projIdx < Main.maxProjectiles)
                {
                    Projectile proj = Main.projectile[projIdx];
                    proj.tileCollide = false;
                    proj.timeLeft = 60; // Tepat 1 detik (60 tick)
                    proj.usesLocalNPCImmunity = true;
                    proj.localNPCHitCooldown = 6;
                    NetMessage.SendData((int)PacketTypes.ProjectileNew, -1, -1, null, projIdx);

                    activeCompassKnives.Add(new CompassKnifeData
                    {
                        ProjIndex = projIdx,
                        TargetNpcIndex = targetNpc.whoAmI,
                        OwnerIndex = player.whoAmI,
                        DirectionIndex = dir,
                        TicksLeft = 60
                    });
                }
            }
        }

        private void OnGameUpdate(EventArgs args)
        {
            // 1. Simulasi 2x Attack Speed saat pemain menggunakan Flying Knife
            for (int i = 0; i < Main.maxPlayers; i++)
            {
                if (extraAttackCooldown[i] > 0)
                    extraAttackCooldown[i]--;

                TSPlayer tsPlayer = TShock.Players[i];
                if (tsPlayer == null || !tsPlayer.Active || tsPlayer.TPlayer == null)
                    continue;

                if (!tsPlayer.HasPermission("flyingknife.buff"))
                    continue;

                Player player = tsPlayer.TPlayer;
                Item heldItem = player.HeldItem;

                if (heldItem != null && heldItem.type == ItemID.FlyingKnife && player.controlUseItem && extraAttackCooldown[i] <= 0)
                {
                    int halfCooldown = Math.Max(4, heldItem.useTime / 2);
                    extraAttackCooldown[i] = halfCooldown;

                    int nerfedDamage = Math.Max(1, (int)(player.GetWeaponDamage(heldItem) * 0.85f));
                    Vector2 shootVel = new Vector2(player.direction * 18f, -4f);

                    int extraProj = Projectile.NewProjectile(
                        player.GetProjectileSource_Item(heldItem),
                        player.MountedCenter,
                        shootVel,
                        ProjectileID.FlyingKnife,
                        nerfedDamage,
                        heldItem.knockBack,
                        player.whoAmI
                    );

                    if (extraProj >= 0 && extraProj < Main.maxProjectiles)
                    {
                        Main.projectile[extraProj].timeLeft = 24;
                        NetMessage.SendData((int)PacketTypes.ProjectileNew, -1, -1, null, extraProj);
                    }
                }
            }

            // 2. Update gerakan 4 Pisau Kompas selama 1 detik (60 tick) agar terus mengikis target
            for (int k = activeCompassKnives.Count - 1; k >= 0; k--)
            {
                CompassKnifeData data = activeCompassKnives[k];
                data.TicksLeft--;

                if (data.ProjIndex < 0 || data.ProjIndex >= Main.maxProjectiles)
                {
                    activeCompassKnives.RemoveAt(k);
                    continue;
                }

                Projectile proj = Main.projectile[data.ProjIndex];

                if (data.TicksLeft <= 0 || !proj.active || proj.type != ProjectileID.FlyingKnife)
                {
                    if (proj.active && proj.type == ProjectileID.FlyingKnife)
                    {
                        proj.Kill();
                        NetMessage.SendData((int)PacketTypes.ProjectileDestroy, -1, -1, null, proj.whoAmI, proj.owner);
            
                    }
                    activeCompassKnives.RemoveAt(k);
                    continue;
                }

                if (data.TargetNpcIndex >= 0 && data.TargetNpcIndex < Main.maxNPCs && Main.npc[data.TargetNpcIndex].active)
                {
                    NPC target = Main.npc[data.TargetNpcIndex];

                    float wave = (float)Math.Sin((60 - data.TicksLeft) * 0.55f) * 65f;
                    Vector2 dirVec = data.DirectionIndex switch
                    {
                        0 => new Vector2(0f, -1f), // Utara
                        1 => new Vector2(1f, 0f),  // Timur
                        2 => new Vector2(0f, 1f),  // Selatan
                        _ => new Vector2(-1f, 0f)  // Barat
                    };

                    Vector2 desiredPos = target.Center + dirVec * wave;
                    proj.velocity = (desiredPos - proj.Center) * 0.65f;
                    proj.rotation += 0.6f;
                    proj.netUpdate = true;
                }
            }
        }
    }
}
