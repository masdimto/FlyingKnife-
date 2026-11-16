using System;
using System.Collections.Generic;
using System.IO;
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
        public override System.Version Version => new System.Version(1, 3, 1);
        public override string Author => "CustomPlugin";
        public override string Description => "Buff Flying Knife: 2x attack speed (-15% damage) dan 4 pisau formasi kompas setiap 4x damage selama 1 detik (reset 4 detik).";

        private class CompassKnifeData
        {
            public int ProjIndex;
            public int TargetNpcIndex;
            public int DirectionIndex; // 0 = Utara, 1 = Timur, 2 = Selatan, 3 = Barat
            public int TicksLeft;
            public int Damage;
        }

        private int[] hitCounter = new int[Main.maxPlayers];
        private int[] resetTimerTicks = new int[Main.maxPlayers];
        private int[] extraAttackCooldown = new int[Main.maxPlayers];
        private List<CompassKnifeData> activeCompassKnives = new List<CompassKnifeData>();

        public FlyingKnifeBuff(Main game) : base(game) { }

        public override void Initialize()
        {
            ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);
            ServerApi.Hooks.NetGetData.Register(this, OnGetData);
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
                ServerApi.Hooks.NetGetData.Deregister(this, OnGetData);
                ServerApi.Hooks.ServerJoin.Deregister(this, OnServerJoin);
                ServerApi.Hooks.ServerLeave.Deregister(this, OnServerLeave);
            }
            base.Dispose(disposing);
        }

        private void CheckStatusCommand(CommandArgs args)
        {
            args.Player.SendSuccessMessage("[FlyingKnifeBuff] Aktif! Attack speed 2x (-15% damage), setiap 4x damage memanggil 4 pisau kompas selama 1 detik (reset 4 detik).");
        }

        private void OnServerJoin(JoinEventArgs args)
        {
            if (args.Who >= 0 && args.Who < Main.maxPlayers)
            {
                hitCounter[args.Who] = 0;
                resetTimerTicks[args.Who] = 0;
                extraAttackCooldown[args.Who] = 0;
            }
        }

        private void OnServerLeave(LeaveEventArgs args)
        {
            if (args.Who >= 0 && args.Who < Main.maxPlayers)
            {
                hitCounter[args.Who] = 0;
                resetTimerTicks[args.Who] = 0;
                extraAttackCooldown[args.Who] = 0;
            }
        }

        private void OnGetData(GetDataEventArgs args)
        {
            if (args.Handled || args.Msg.whoAmI < 0 || args.Msg.whoAmI >= Main.maxPlayers)
                return;

            // Packet 28: NpcStrike
            if (args.MsgID == PacketTypes.NpcStrike)
            {
                int plrIdx = args.Msg.whoAmI;
                TSPlayer tsPlayer = TShock.Players[plrIdx];
                if (tsPlayer == null || !tsPlayer.Active || tsPlayer.TPlayer == null)
                    return;

                if (!tsPlayer.HasPermission("flyingknife.buff"))
                    return;

                Player player = tsPlayer.TPlayer;
                Item heldItem = player.HeldItem;

                if (heldItem == null || heldItem.type != ItemID.FlyingKnife)
                    return;

                using (MemoryStream ms = new MemoryStream(args.Msg.readBuffer, args.Index, args.Length))
                using (BinaryReader reader = new BinaryReader(ms))
                {
                    short npcId = reader.ReadInt16();
                    short damage = reader.ReadInt16();

                    if (damage <= 0 || npcId < 0 || npcId >= Main.maxNPCs || !Main.npc[npcId].active)
                        return;

                    // Reset timer 4 detik (240 tick) setiap kali memberikan damage
                    resetTimerTicks[plrIdx] = 240;
                    hitCounter[plrIdx]++;

                    if (hitCounter[plrIdx] >= 4)
                    {
                        hitCounter[plrIdx] = 0;
                        resetTimerTicks[plrIdx] = 0;

                        int nerfedDamage = Math.Max(1, (int)(player.GetWeaponDamage(heldItem) * 0.85f));
                        SpawnCompassKnives(player, Main.npc[npcId], nerfedDamage, heldItem.knockBack);
                    }
                }
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
                Vector2 velocity = toTarget * 24f;

                int projIdx = Projectile.NewProjectile(
                    Projectile.GetNoneSource(),
                    spawnPos,
                    velocity,
                    ProjectileID.ShadowFlameKnife,
                    damage,
                    knockBack * 0.1f,
                    Main.maxPlayers
                );

                if (projIdx >= 0 && projIdx < Main.maxProjectiles)
                {
                    Projectile proj = Main.projectile[projIdx];
                    proj.friendly = true;
                    proj.hostile = false;
                    proj.tileCollide = false;
                    proj.penetrate = -1;
                    proj.timeLeft = 60; // Tepat 1 detik (60 tick)
                    proj.usesLocalNPCImmunity = true;
                    proj.localNPCHitCooldown = 6;
                    NetMessage.SendData((int)PacketTypes.ProjectileNew, -1, -1, null, projIdx);

                    activeCompassKnives.Add(new CompassKnifeData
                    {
                        ProjIndex = projIdx,
                        TargetNpcIndex = targetNpc.whoAmI,
                        DirectionIndex = dir,
                        TicksLeft = 60,
                        Damage = damage
                    });
                }
            }
        }

        private void OnGameUpdate(EventArgs args)
        {
            for (int i = 0; i < Main.maxPlayers; i++)
            {
                // 1. Timer reset 4 detik (240 tick)
                if (resetTimerTicks[i] > 0)
                {
                    resetTimerTicks[i]--;
                    if (resetTimerTicks[i] <= 0)
                    {
                        hitCounter[i] = 0;
                    }
                }

                // 2. Simulasi 2x Attack Speed (-15% damage)
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
                    int halfCooldown = Math.Max(5, heldItem.useTime / 2);
                    extraAttackCooldown[i] = halfCooldown;

                    int nerfedDamage = Math.Max(1, (int)(player.GetWeaponDamage(heldItem) * 0.85f));
                    Vector2 shootVel = new Vector2(player.direction * 22f, -2f);

                    int extraProj = Projectile.NewProjectile(
                        Projectile.GetNoneSource(),
                        player.MountedCenter,
                        shootVel,
                        ProjectileID.ShadowFlameKnife,
                        nerfedDamage,
                        heldItem.knockBack * 0.5f,
                        Main.maxPlayers
                    );

                    if (extraProj >= 0 && extraProj < Main.maxProjectiles)
                    {
                        Projectile ep = Main.projectile[extraProj];
                        ep.friendly = true;
                        ep.hostile = false;
                        ep.timeLeft = 35;
                        NetMessage.SendData((int)PacketTypes.ProjectileNew, -1, -1, null, extraProj);
                    }
                }
            }

            // 3. Gerakkan 4 Pisau Kompas selama 1 detik (60 tick) & pastikan damage masuk ke target
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

                if (data.TicksLeft <= 0 || !proj.active || proj.type != ProjectileID.ShadowFlameKnife)
                {
                    if (proj.active && proj.type == ProjectileID.ShadowFlameKnife)
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

                    // Gerakan cepat maju-mundur menyayat dari 4 arah mata angin
                    float wave = (float)Math.Cos((60 - data.TicksLeft) * 0.65f) * 80f;
                    Vector2 dirVec = data.DirectionIndex switch
                    {
                        0 => new Vector2(0f, -1f), // Utara
                        1 => new Vector2(1f, 0f),  // Timur
                        2 => new Vector2(0f, 1f),  // Selatan
                        _ => new Vector2(-1f, 0f)  // Barat
                    };

                    Vector2 desiredPos = target.Center + dirVec * wave;
                    proj.velocity = (desiredPos - proj.Center) * 0.85f;
                    proj.Center = desiredPos;
                    proj.timeLeft = data.TicksLeft;

                    // Berikan damage langsung setiap 8 tick jika pisau melewati badan musuh
                    if (data.TicksLeft % 8 == 0 && proj.Hitbox.Intersects(target.Hitbox))
                    {
                        target.StrikeNPC(data.Damage, 0f, 0);
                        NetMessage.SendData((int)PacketTypes.NpcStrike, -1, -1, null, target.whoAmI, data.Damage, 0f, 0f, 0);
                    }

                    if (data.TicksLeft % 3 == 0)
                    {
                        NetMessage.SendData((int)PacketTypes.ProjectileNew, -1, -1, null, data.ProjIndex);
                    }
                }
            }
        }
    }
}
