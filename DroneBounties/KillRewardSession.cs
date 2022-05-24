﻿using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Weapons;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Utils;

namespace DroneBounties
{
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    partial class KillRewardSession : MySessionComponentBase
    {
        private const int DamageEventExpirationInSeconds = 300;

        private List<IMyPlayer> Players = new List<IMyPlayer>();
        private Dictionary<long, List<GridDamageEvent>> _gridDamageEvents;

        //TODO: Make this externally configurable
        private List<string> _validSubtypeIds = new List<string> { "RivalAIRemoteControlLarge", "RivalAIRemoteControlSmall" };

        public override void BeforeStart()
        {
            MyAPIGateway.Session.DamageSystem.RegisterAfterDamageHandler(int.MinValue, AfterDamageHandler);

        }

        public override void LoadData()
        {
            MyLog.Default.WriteLine("PvE.KillRewardSession: loading v4");
            if (!MyAPIGateway.Session.IsServer)
            {
                MyLog.Default.WriteLine("PvE.KillRewardSession: Canceling load, not a server.");
                return;
            }

            _gridDamageEvents = new Dictionary<long, List<GridDamageEvent>>();

            MyVisualScriptLogicProvider.BlockDestroyed += OnBlockDestroyed;


            MyLog.Default.WriteLine("PvE.KillRewardSession: loaded...");
        }

        private void AfterDamageHandler(object damagedObject, MyDamageInformation damageInfo)
        {
            try
            {
                var victimCubeGrid = (damagedObject as IMySlimBlock)?.CubeGrid;
                if (victimCubeGrid == null) return;

                var attackerId = damageInfo.AttackerId;
                var damage = damageInfo.Amount;

                Players.Clear();
                if (MyAPIGateway.Multiplayer.MultiplayerActive)
                {
                    MyAPIGateway.Multiplayer.Players.GetPlayers(Players);
                }
                else
                {
                    MyAPIGateway.Players.GetPlayers(Players);
                }

                var entity = MyAPIGateway.Entities.GetEntityById(attackerId);
                var attackerCubeGrid = (entity as IMyCubeBlock)?.CubeGrid;

                //Attempt 1: Find the player ref by first matching entityId. Don't know if this is a thing.
                IMyPlayer attackingPlayer = Players.SingleOrDefault(_player => _player.Character != null && _player.Character.EntityId == attackerId);

                //Attempt 2: Find the player by who is controlling the grid
                if (attackingPlayer == null)
                {
                    attackingPlayer = MyAPIGateway.Players.GetPlayerControllingEntity(attackerCubeGrid);
                }

                //Attempt 3: Fallback to a couple other miscellaneous methods
                var attackerIdentityId = (attackingPlayer == null) ? GetAttackerIdentityId(attackerId) : attackingPlayer.IdentityId;

                GridDamageEvent damageEvent = new GridDamageEvent()
                {
                    AttackerIdentityId = attackerIdentityId, // I did the damage
                    VictimEntityId = victimCubeGrid.EntityId, // I was damaged
                    Damage = damage, // How much damage
                    TimestampTicks = DateTime.Now.Ticks // When the damage was done
                };

                List<GridDamageEvent> gridDamageEvents = null;
                if (_gridDamageEvents.ContainsKey(victimCubeGrid.EntityId))
                {
                    if (!_gridDamageEvents.TryGetValue(victimCubeGrid.EntityId, out gridDamageEvents))
                    {
                        MyLog.Default.WriteLine("PvE.KillReward: Unable to access grid events. Concurrency issue?");
                        //TODO: Realistic issue?
                    }
                }
                else
                {
                    gridDamageEvents = new List<GridDamageEvent>();
                    _gridDamageEvents.Add(victimCubeGrid.EntityId, gridDamageEvents);
                }

                if (gridDamageEvents != null)
                {
                    gridDamageEvents.Add(damageEvent);
                }
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine($"PvE.KillReward.AfterDamageHandler: {ex}");
            }
        }

        protected override void UnloadData()
        {
            MyVisualScriptLogicProvider.BlockDestroyed -= OnBlockDestroyed;
        }

        private void OnBlockDestroyed(string entityName, string gridName, string typeId, string subtypeId)
        {

            MyLog.Default.WriteLine("PvE.KillReward: OnBlockDestroyed");
            //No attackerId, so instead we track list of attackers in OnDamage and they're damage in a dictionary and then assign rewards on "kill" appropriately

            //Janitorial things first, cleanup expired damage events
            CleanupExpiredDamageEvents(); //I liked having this in the finally but then we had to manually cleanup in the code below, DRY

            try
            {
                if (entityName.Length == 0) return;

                //TODO: Verify if this is simply returning the block type, or the actual instance?
                var entity = MyAPIGateway.Entities.GetEntityByName(entityName);
                if (entity == null) return;

                //Require victim block to be a remote control, and specifically one of the Rival AI ones (by default)
                var victimRemoteBlock = (entity as IMyRemoteControl);
                if (victimRemoteBlock == null) return;

                //if (victimRemoteBlock == null || !_validSubtypeIds.Contains(subtypeId)) return;

                //Fetch grid reference
                var victimCubeGrid = victimRemoteBlock.CubeGrid;
                if (victimCubeGrid == null) return;

                //Test if we are even tracking damage events for the target grid, no point doing any extra work. This value is used later though.
                var victimGridEntityId = victimCubeGrid.EntityId;
                if (!_gridDamageEvents.ContainsKey(victimGridEntityId))
                {
                    MyLog.Default.WriteLine("PvE.KillReward: No damage events for grid.");
                    //unable to find any damage events for this grid. They either expired or the block was deleted/exploded for some other reason.
                    return;
                }

                //TODO: CustomData stuff only works with specific types of functional blocks, should narrow that down in the future instead of hard coding remote controls
                //Load bounty parameters from the custom data of the block
                bool successful = true;
                string result = "";

                //TODO: verbose, but preparing for adding additional parameters
                //Block/grid self-defines bounty
                int bountyOnKill = 0;
                if (CustomDataToConfig(victimRemoteBlock.CustomData, ref result, "BountyOnKill"))
                {
                    successful &= int.TryParse(result, out bountyOnKill);
                }

                if (!successful)
                {
                    //Hiding these log lines, could be spammy if most blocks don't have bounties configured??
                    //MyLog.Default.WriteLine("Unable to find bounty info in customData");
                    //MyLog.Default.WriteLine($"customData={victimRemoteBlock.CustomData}");
                    //TODO: optionally, don't bail and assign using a default bounty multiplier TBD.
                    return;
                }

                //MyLog.Default.WriteLine($"PvE.KillReward: Should issue bounty for {bountyOnKill}.");

                //For relations checks, we use first majority owner.
                //TODO: Figure out what this is returning. A player identity? A faction ID?
                var victimIdentityId = (victimCubeGrid.BigOwners != null && victimCubeGrid.BigOwners.Count > 0) ? victimCubeGrid.BigOwners[0] : 0L;

                //TODO: Determine if it is really necessary to do this on every event? Is there not a better way to keep it in sync?
                Players.Clear();
                if (MyAPIGateway.Multiplayer.MultiplayerActive)
                {
                    MyAPIGateway.Multiplayer.Players.GetPlayers(Players);
                }
                else
                {
                    MyAPIGateway.Players.GetPlayers(Players);
                }

                //Verified above that _gridDamageEvents contains the key victimGridEntityId
                List<GridDamageEvent> gridDamageEvents;
                if (!_gridDamageEvents.TryGetValue(victimGridEntityId, out gridDamageEvents))
                {
                    MyLog.Default.WriteLine("PvE.KillReward: Unable to access grid events. Concurrency issue?");
                    //TODO: Realistic issue?
                    return;
                }

                //MyLog.Default.WriteLine($"PvE.KillReward: Number of damage events: {gridDamageEvents.Count}.");

                //Group by attacker, track their cumulative damage
                var damageByAttackers = gridDamageEvents.GroupBy(de => de.AttackerIdentityId, (attackerIdentityId, damageEvents) => new
                {
                    AttackerIdentityId = attackerIdentityId,
                    CumulativeDamage = damageEvents.Sum(de2 => de2.Damage),
                    Player = Players.SingleOrDefault(p => p?.IdentityId == attackerIdentityId)
                });

                //MyLog.Default.WriteLine($"PvE.KillReward: Number of attackers: {damageByAttackers.Count()}.");

                //Filter out friendly fire
                var validAttackers = damageByAttackers.Where(attacker =>
                {
                    var attackerIdentityId = attacker.AttackerIdentityId;
                    var relation = MyIDModule.GetRelationPlayerPlayer(attackerIdentityId, victimIdentityId);
                    return attacker.Player != null && relation == MyRelationsBetweenPlayers.Enemies;
                }).ToList();

                //MyLog.Default.WriteLine($"PvE.KillReward: Number of enemies: {validAttackers.Count}.");

                //This is how much damage was done to the grid in total by valid attackers within the last DamageEventExpirationInSeconds
                var totalDamage = validAttackers.Sum(va => va.CumulativeDamage);

                //MyLog.Default.WriteLine($"PvE.KillReward: CumulativeDamage: {totalDamage}.");
                
                //Figure out the proportion of total damage done by each attacker and assign their proportion of the monies
                foreach (var attacker in validAttackers)
                {
                    var proportionalDamage = attacker.CumulativeDamage / totalDamage;
                    var proportionalBounty = (long)(proportionalDamage * bountyOnKill);

                    attacker.Player.RequestChangeBalance(proportionalBounty);

                    //MyLog.Default.WriteLine($"PvE.KillReward: Assigning {proportionalBounty} to {attacker.Player.DisplayName}.");

                    MyAPIGateway.Utilities.ShowMessage("PvE", $"{victimCubeGrid.DisplayName} Killed - Assigning {proportionalBounty} bounty to {attacker.Player.DisplayName}.");
                }

                //In theory we shouldn't have to track this grid anymore, there should only be a single trophy block per grid (we're assuming)
                _gridDamageEvents.Remove(victimGridEntityId);
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine($"PvE.KillReward.OnBlockDestroyed: {ex}");
            }
        }

        /// <summary>
        /// Purges expired damage events
        /// </summary>
        private void CleanupExpiredDamageEvents()
        {
            var ticksDeadline = DateTime.Now.Add(TimeSpan.FromSeconds(-DamageEventExpirationInSeconds)).Ticks;
            foreach (var gridEntity in _gridDamageEvents)
            {
                var gridDamageEvents = gridEntity.Value;
                gridDamageEvents.RemoveAll(de => de.TimestampTicks < ticksDeadline);
            }

            //TODO: remove grids with empty damage event lists
        }

        #region Helper Statics
        internal static bool CustomDataToConfig(string input, ref string output, string search)
        {
            //Keep method static and regex cache will re-use compiled version
            MatchCollection matchcollection = Regex.Matches(input, search + @"\s?=\s?(.+?);", RegexOptions.Compiled);
            foreach (Match match in matchcollection)
            {
                if (match.Groups.Count == 2)
                {
                    output = (match.Groups[1].Value.Trim());
                    return true;
                }
                else
                    return false;
            }
            return false;
        }

        public static bool IsIdentityPlayer(long id)
        {
            return MyAPIGateway.Players.TryGetSteamId(id) > 0;
        }

        // Borrowed from: https://steamcommunity.com/sharedfiles/filedetails/?id=2495746295 Unsure if they were original author of this method.
        // My understanding is it's getting the identity of the person controlling a block that has done the damage, or it's the identity of the charcter holding the gun
        internal static long GetAttackerIdentityId(long attackerId)
        {
            var entity = MyAPIGateway.Entities.GetEntityById(attackerId);

            var cubeGrid = entity as IMyCubeGrid;
            if (cubeGrid != null) return (cubeGrid.BigOwners.Count > 0) ? cubeGrid.BigOwners[0] : 0;

            var myControllableEntity = entity as VRage.Game.ModAPI.Interfaces.IMyControllableEntity;
            if (myControllableEntity != null)
            {
                var controllerInfo = myControllableEntity.ControllerInfo;

                if (controllerInfo != null) return controllerInfo.ControllingIdentityId;
            }
            else
            {
                IMyGunBaseUser myGunBaseUser;
                if ((myGunBaseUser = (entity as IMyGunBaseUser)) != null) return myGunBaseUser.OwnerId;

                IMyHandheldGunObject<MyDeviceBase> myHandheldGunObject;
                if ((myHandheldGunObject = (entity as IMyHandheldGunObject<MyDeviceBase>)) != null) return myHandheldGunObject.OwnerIdentityId;
            }

            return 0L;
        }
        #endregion
    }
}
