using Sandbox.Game;
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
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    partial class KillRewardSession : MySessionComponentBase
    {
        private const int DamageEventExpirationInSeconds = 300;

        private List<IMyPlayer> Players = new List<IMyPlayer>();
        private Dictionary<long, List<GridDamagedEvent>> _gridDamageEvents = new Dictionary<long, List<GridDamagedEvent>>();
        private Dictionary<long, List<MyDamageInformation>> _damageInfos = new Dictionary<long, List<MyDamageInformation>>();
        private Dictionary<long, List<GridDestroyedEvent>> _blockDestroyedEvents = new Dictionary<long, List<GridDestroyedEvent>>();
        //TODO: Make this externally configurable
        private List<string> _validSubtypeIds = new List<string> { "RivalAIRemoteControlLarge", "RivalAIRemoteControlSmall" };

        private long _lastRunTicks = 0L;
        private int _msBetweenUpdates = 1000;

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

            MyVisualScriptLogicProvider.BlockDestroyed += OnBlockDestroyed;

            MyLog.Default.WriteLine("PvE.KillRewardSession: loaded...");
        }

        public override void UpdateAfterSimulation()
        {
            if( (_lastRunTicks + _msBetweenUpdates * TimeSpan.TicksPerMillisecond) < DateTime.Now.Ticks)
            { 
                //Janitorial things first, cleanup expired damage events
                CleanupExpiredDamageEvents();
                UpdatePlayers();         
                ProcessQueuedDamageInfo();
                ProcessQueuedKills();

                _lastRunTicks = DateTime.Now.Ticks;
            }
        }

        public void ProcessQueuedDamageInfo()
        {
            try
            {
                foreach (var victimEntityId in _damageInfos.Keys)
                {
                    List<MyDamageInformation> myDamages;
                    if (_damageInfos.TryGetValue(victimEntityId, out myDamages))
                    {
                        var attackerDamageInfo = myDamages.GroupBy(md => md.AttackerId);
                        foreach (var attackerGroup in attackerDamageInfo)
                        {
                            var attackerId = attackerGroup.Key;
                            var damage = attackerGroup.Sum(md => md.Amount);

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

                            GridDamagedEvent damageEvent = new GridDamagedEvent()
                            {
                                AttackerIdentityId = attackerIdentityId, // I did the damage
                                VictimEntityId = victimEntityId, // I was damaged
                                Damage = damage, // How much damage
                                TimestampTicks = DateTime.Now.Ticks // When the damage was done (somewhat close, anyhow)
                            };

                            List<GridDamagedEvent> gridDamageEvents = null;
                            if (_gridDamageEvents.ContainsKey(victimEntityId))
                            {
                                if (!_gridDamageEvents.TryGetValue(victimEntityId, out gridDamageEvents))
                                {
                                    MyLog.Default.WriteLine("PvE.KillReward: Unable to access grid events. Concurrency issue?");
                                    //TODO: Realistic issue?
                                }
                            }
                            else
                            {
                                gridDamageEvents = new List<GridDamagedEvent>();
                                _gridDamageEvents.Add(victimEntityId, gridDamageEvents);
                            }

                            if (gridDamageEvents != null)
                            {
                                gridDamageEvents.Add(damageEvent);
                            }
                        }
                    }
                }

                //We have processed all of the DamageInfos, go ahead and clear the list.
                _damageInfos.Clear();
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine($"PvE.KillReward.ProcessQueuedDamageInfo: {ex}");
            }
        }

        public void ProcessQueuedKills()
        {
            try
            {
                foreach (var victimEntityId in _blockDestroyedEvents.Keys)
                {
                    List<GridDestroyedEvent> destroyedGridEvents;
                    if (_blockDestroyedEvents.TryGetValue(victimEntityId, out destroyedGridEvents))
                    {
                        foreach (var dg in destroyedGridEvents)
                        {
                            ProcessQueuedKill(dg.VictimEntityId, dg.VictimIdentityId, dg.BountyOnKill, dg.VictimGridDisplayName);
                        }
                    }
                }

                //We have processed all of the BlockDestroyedEvents, go ahead and clear the list.
                _blockDestroyedEvents.Clear();
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine($"PvE.KillReward.ProcessQueuedKills: {ex}");
            }
        }

        public void ProcessQueuedKill(long victimGridEntityId, long victimIdentityId, int bountyOnKill, string victimGridName)
        { 
            try
            {
                //Test if we are even tracking damage events for the target grid, no point doing any extra work. This value is used later though.
                if (!_gridDamageEvents.ContainsKey(victimGridEntityId))
                {
                    MyLog.Default.WriteLine("PvE.KillReward: No damage events for grid.");
                    //unable to find any damage events for this grid. They either expired or the block was deleted/exploded for some other reason.
                    return;
                }

                //Verified above that _gridDamageEvents contains the key victimGridEntityId
                List<GridDamagedEvent> gridDamageEvents;
                if (!_gridDamageEvents.TryGetValue(victimGridEntityId, out gridDamageEvents))
                {
                    MyLog.Default.WriteLine("PvE.KillReward: Unable to access grid events. Concurrency issue?");
                    //TODO: Realistic issue?
                    return;
                }

                //MyLog.Default.WriteLine($"PvE.KillReward: Number of damage events: {gridDamageEvents.Count}.");
                //MyLog.Default.WriteLine($"PvE.KillReward: Grid damage: {gridDamageEvents.Sum(gd => gd.Damage)}.");

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

                    if (proportionalBounty > 0)
                    {
                        attacker.Player.RequestChangeBalance(proportionalBounty);
                        //MyLog.Default.WriteLine($"PvE.KillReward: Assigning {proportionalBounty} to {attacker.Player.DisplayName}.");
                        MyAPIGateway.Utilities.ShowMessage("PvE", $"{victimGridName} Killed - Assigning {proportionalBounty} bounty to {attacker.Player.DisplayName}.");
                    }
                }

                //In theory we shouldn't have to track this grid anymore, there should only be a single trophy block per grid (we're assuming)
                _gridDamageEvents.Remove(victimGridEntityId);
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine($"PvE.KillReward.ProcessQueuedKill: {ex}");
            }
        }

        private void AfterDamageHandler(object damagedObject, MyDamageInformation damageInfo)
        {
            try
            {
                var victimCubeGrid = (damagedObject as IMySlimBlock)?.CubeGrid;
                if (victimCubeGrid == null) return;

                var victimEntityId = victimCubeGrid.EntityId;
                List<MyDamageInformation> damageInfoList;
                if (!_damageInfos.TryGetValue(victimEntityId, out damageInfoList))
                {
                    damageInfoList = new List<MyDamageInformation>();
                    _damageInfos.Add(victimEntityId, damageInfoList);
                }
                damageInfoList.Add(damageInfo);
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
            //No attackerId, so instead we track list of attackers in OnDamage and they're damage in a dictionary and then assign rewards on "kill" appropriately

            // Damage events were refactored to be clustered together better. Unfortunately, that forces us to do the same with block destroyed events
            // or they could happen *before* the damage events are all fully processed.
            // However, it's unclear what the lifecycle of destroyed blocks is and how much can actually be deferred, so we'll do some key things here that
            // assume the worst, the targets are dead and inaccessible once we leave this function.

            //Log all function parameters for debug purposes
            //MyLog.Default.WriteLine($"PvE.KillReward.OnBlockDestroyed: entityName={entityName}, gridName={gridName}, typeId={typeId}, subtypeId={subtypeId}");

            try
            {
                if (string.IsNullOrEmpty(entityName)) return;

                //TODO: Verify if this is simply returning the block type, or the actual instance?
                var entity = MyAPIGateway.Entities.GetEntityByName(entityName);
                if (entity == null) return;

                //Require victim block to be a remote control, and specifically one of the Rival AI ones (by default)
                var victimRemoteBlock = (entity as IMyRemoteControl);
                // TODO: If MESApi is valid and loaded, we should enforce the Rival AI subtypes
                if (victimRemoteBlock == null || !_validSubtypeIds.Contains(subtypeId))
                {
                    //MyLog.Default.WriteLine($"PvE.KillReward.OnBlockDestroyed: Block is of type {subtypeId}");
                    return;
                };

                //Fetch grid reference
                var victimCubeGrid = victimRemoteBlock.CubeGrid;
                if (victimCubeGrid == null) return;

                //Grab grid id, we need this to group damage events
                var victimGridEntityId = victimCubeGrid.EntityId;

                //For relations checks, we use first majority owner.
                //TODO: Figure out what this is returning. A player identity? A faction ID?
                var victimIdentityId = (victimCubeGrid.BigOwners != null && victimCubeGrid.BigOwners.Count > 0) ? victimCubeGrid.BigOwners[0] : 0L;

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
                    //Hiding these log lines, could be spammy if most blocks don't have bounties configured
                    //MyLog.Default.WriteLine("Unable to find bounty info in customData");
                    //MyLog.Default.WriteLine($"customData={victimRemoteBlock.CustomData}");
                    //TODO: optionally, don't bail and assign using a default bounty multiplier TBD.
                    return;
                }

                //MyLog.Default.WriteLine($"PvE.KillReward: Should issue bounty for {bountyOnKill}.");

                List<GridDestroyedEvent> destroyedGridEvents;
                if (!_blockDestroyedEvents.TryGetValue(victimGridEntityId, out destroyedGridEvents))
                {
                    destroyedGridEvents = new List<GridDestroyedEvent>();
                    _blockDestroyedEvents.Add(victimGridEntityId, destroyedGridEvents);
                }
                destroyedGridEvents.Add(new GridDestroyedEvent()
                {
                    VictimGridDisplayName = victimCubeGrid.DisplayName,
                    VictimEntityId = victimGridEntityId,
                    VictimIdentityId = victimIdentityId,
                    BountyOnKill = bountyOnKill
                });
            }
            catch (Exception ex)
            {
                //MyLog.Default.WriteLine($"PvE.KillReward.OnBlockDestroyed: {ex}");
            }
        }

        private void UpdatePlayers()
        {
            //TODO: Add some throttling here, don't update player list constantly
            Players.Clear();
            if (MyAPIGateway.Multiplayer.MultiplayerActive)
            {
                MyAPIGateway.Multiplayer.Players.GetPlayers(Players);
            }
            else
            {
                MyAPIGateway.Players.GetPlayers(Players);
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
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(search))
            {
                return false;
            }

            try
            {
                // Keep method static and regex cache will re-use compiled version
                MatchCollection matchCollection = Regex.Matches(input, search + @"\s?=\s?(.+?);", RegexOptions.Compiled);

                foreach (Match match in matchCollection)
                {
                    if (match.Groups.Count == 2)
                    {
                        output = match.Groups[1].Value.Trim();
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                //MyLog.Default.WriteLine($"PvE.KillReward.CustomDataToConfig: {ex}");
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

            if (entity == null) return 0L;

            //TODO Determine if this is a valid case, and if so, how to handle it.
            //if(entity is IMyCharacter)
            //{
            //    var character = entity as IMyCharacter;
            //    MyLog.Default.WriteLine($"PvE.KillReward.GetAttackerIdentityId: Attacker is player.");
            //    return character.EntityId;
            //}

            if (entity is IMyCubeGrid)
            {
                var cubeGrid = entity as IMyCubeGrid;
                if (cubeGrid != null) return (cubeGrid.BigOwners.Count > 0) ? cubeGrid.BigOwners[0] : 0; 
            }

            if(entity is IMyCubeBlock)
            {
                var cubeGrid = (entity as IMyCubeBlock).CubeGrid;
                if (cubeGrid != null) return (cubeGrid.BigOwners.Count > 0) ? cubeGrid.BigOwners[0] : 0;
            }

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
