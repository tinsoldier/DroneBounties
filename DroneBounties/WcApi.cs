using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRage.Game;
using VRage.ModAPI;
using VRageMath;
using VRage;

namespace DroneBounties
{
    /// <summary>
    /// https://github.com/sstixrud/WeaponCore/blob/master/Data/Scripts/WeaponCore/Api/WeaponCoreApi.cs
    /// </summary>
    /// 

    public class WcApi
	{
		private bool _apiInit;

		private Action<IList<byte[]>> _getAllWeaponDefinitions;
		private Action<ICollection<MyDefinitionId>> _getCoreWeapons;
		private Action<ICollection<MyDefinitionId>> _getCoreStaticLaunchers;
		private Action<ICollection<MyDefinitionId>> _getCoreTurrets;
		private Func<IMyTerminalBlock, IDictionary<string, int>, bool> _getBlockWeaponMap;
		private Func<IMyEntity, MyTuple<bool, int, int>> _getProjectilesLockedOn;
		private Action<IMyEntity, ICollection<MyTuple<IMyEntity, float>>> _getSortedThreats;
		private Func<IMyEntity, int, IMyEntity> _getAiFocus;
		private Func<IMyEntity, IMyEntity, int, bool> _setAiFocus;
		private Func<IMyTerminalBlock, int, MyTuple<bool, bool, bool, IMyEntity>> _getWeaponTarget;
		private Action<IMyTerminalBlock, IMyEntity, int> _setWeaponTarget;
		private Action<IMyTerminalBlock, bool, int> _fireWeaponOnce;
		private Action<IMyTerminalBlock, bool, bool, int> _toggleWeaponFire;
		private Func<IMyTerminalBlock, int, bool, bool, bool> _isWeaponReadyToFire;
		private Func<IMyTerminalBlock, int, float> _getMaxWeaponRange;
		private Func<IMyTerminalBlock, ICollection<string>, int, bool> _getTurretTargetTypes;
		private Action<IMyTerminalBlock, ICollection<string>, int> _setTurretTargetTypes;
		private Action<IMyTerminalBlock, float> _setBlockTrackingRange;
		private Func<IMyTerminalBlock, IMyEntity, int, bool> _isTargetAligned;
		private Func<IMyTerminalBlock, IMyEntity, int, MyTuple<bool, Vector3D?>> _isTargetAlignedExtended;
		private Func<IMyTerminalBlock, IMyEntity, int, bool> _canShootTarget;
		private Func<IMyTerminalBlock, IMyEntity, int, Vector3D?> _getPredictedTargetPos;
		private Func<IMyTerminalBlock, float> _getHeatLevel;
		private Func<IMyTerminalBlock, float> _currentPowerConsumption;
		private Func<MyDefinitionId, float> _getMaxPower;
		private Action<IMyTerminalBlock> _disableRequiredPower;
		private Func<IMyEntity, bool> _hasGridAi;
		private Func<IMyTerminalBlock, bool> _hasCoreWeapon;
		private Func<IMyEntity, float> _getOptimalDps;
		private Func<IMyTerminalBlock, int, string> _getActiveAmmo;
		private Action<IMyTerminalBlock, int, string> _setActiveAmmo;
		private Action<IMyTerminalBlock, int, Action<long, int, ulong, long, Vector3D, bool>> _monitorProjectile;
		private Action<IMyTerminalBlock, int, Action<long, int, ulong, long, Vector3D, bool>> _unMonitorProjectile;
		private Func<ulong, MyTuple<Vector3D, Vector3D, float, float, long, string>> _getProjectileState;
		private Func<IMyEntity, float> _getConstructEffectiveDps;
		private Func<IMyTerminalBlock, long> _getPlayerController;
		private Func<IMyTerminalBlock, int, Matrix> _getWeaponAzimuthMatrix;
		private Func<IMyTerminalBlock, int, Matrix> _getWeaponElevationMatrix;
		private Func<IMyTerminalBlock, IMyEntity, bool, bool, bool> _isTargetValid;
		private Func<IMyTerminalBlock, int, MyTuple<Vector3D, Vector3D>> _getWeaponScope;
		private Func<IMyEntity, MyTuple<bool, bool>> _isInRange;
		private const long Channel = 67549756549;
		private bool _getWeaponDefinitions;
		private bool _isRegistered;
		private Action _readyCallback;

		/// <summary>
		/// True if the WeaponCore replied when <see cref="Load"/> got called.
		/// </summary>
		public bool IsReady { get; private set; }

		/// <summary>
		/// Only filled if giving true to <see cref="Load"/>.
		/// </summary>
		public readonly List<WcApiDef.WeaponDefinition> WeaponDefinitions = new List<WcApiDef.WeaponDefinition>();

		/// <summary>
		/// Ask WeaponCore to send the API methods.
		/// <para>Throws an exception if it gets called more than once per session without <see cref="Unload"/>.</para>
		/// </summary>
		/// <param name="readyCallback">Method to be called when WeaponCore replies.</param>
		/// <param name="getWeaponDefinitions">Set to true to fill <see cref="WeaponDefinitions"/>.</param>
		public void Load(Action readyCallback = null, bool getWeaponDefinitions = false)
		{
			if (_isRegistered)
				throw new Exception($"{GetType().Name}.Load() should not be called multiple times!");

			_readyCallback = readyCallback;
			_getWeaponDefinitions = getWeaponDefinitions;
			_isRegistered = true;
			MyAPIGateway.Utilities.RegisterMessageHandler(Channel, HandleMessage);
			MyAPIGateway.Utilities.SendModMessage(Channel, "ApiEndpointRequest");
		}

		public void Unload()
		{
			MyAPIGateway.Utilities.UnregisterMessageHandler(Channel, HandleMessage);

			ApiAssign(null, false);

			_isRegistered = false;
			_apiInit = false;
			IsReady = false;
		}

		private void HandleMessage(object obj)
		{
			if (_apiInit || obj is string
			) // the sent "ApiEndpointRequest" will also be received here, explicitly ignoring that
				return;

			var dict = obj as IReadOnlyDictionary<string, Delegate>;

			if (dict == null)
				return;

			ApiAssign(dict, _getWeaponDefinitions);

			IsReady = true;
			_readyCallback?.Invoke();
		}

		public void ApiAssign(IReadOnlyDictionary<string, Delegate> delegates, bool getWeaponDefinitions = false)
		{
			_apiInit = delegates != null;

			AssignMethod(delegates, "GetAllWeaponDefinitions", ref _getAllWeaponDefinitions);
			AssignMethod(delegates, "GetCoreWeapons", ref _getCoreWeapons);
			AssignMethod(delegates, "GetCoreStaticLaunchers", ref _getCoreStaticLaunchers);
			AssignMethod(delegates, "GetCoreTurrets", ref _getCoreTurrets);
			AssignMethod(delegates, "GetBlockWeaponMap", ref _getBlockWeaponMap);
			AssignMethod(delegates, "GetProjectilesLockedOn", ref _getProjectilesLockedOn);
			AssignMethod(delegates, "GetSortedThreats", ref _getSortedThreats);
			AssignMethod(delegates, "GetAiFocus", ref _getAiFocus);
			AssignMethod(delegates, "SetAiFocus", ref _setAiFocus);
			AssignMethod(delegates, "GetWeaponTarget", ref _getWeaponTarget);
			AssignMethod(delegates, "SetWeaponTarget", ref _setWeaponTarget);
			AssignMethod(delegates, "FireWeaponOnce", ref _fireWeaponOnce);
			AssignMethod(delegates, "ToggleWeaponFire", ref _toggleWeaponFire);
			AssignMethod(delegates, "IsWeaponReadyToFire", ref _isWeaponReadyToFire);
			AssignMethod(delegates, "GetMaxWeaponRange", ref _getMaxWeaponRange);
			AssignMethod(delegates, "GetTurretTargetTypes", ref _getTurretTargetTypes);
			AssignMethod(delegates, "SetTurretTargetTypes", ref _setTurretTargetTypes);
			AssignMethod(delegates, "SetBlockTrackingRange", ref _setBlockTrackingRange);
			AssignMethod(delegates, "IsTargetAligned", ref _isTargetAligned);
			AssignMethod(delegates, "IsTargetAlignedExtended", ref _isTargetAlignedExtended);
			AssignMethod(delegates, "CanShootTarget", ref _canShootTarget);
			AssignMethod(delegates, "GetPredictedTargetPosition", ref _getPredictedTargetPos);
			AssignMethod(delegates, "GetHeatLevel", ref _getHeatLevel);
			AssignMethod(delegates, "GetCurrentPower", ref _currentPowerConsumption);
			AssignMethod(delegates, "GetMaxPower", ref _getMaxPower);
			AssignMethod(delegates, "DisableRequiredPower", ref _disableRequiredPower);
			AssignMethod(delegates, "HasGridAi", ref _hasGridAi);
			AssignMethod(delegates, "HasCoreWeapon", ref _hasCoreWeapon);
			AssignMethod(delegates, "GetOptimalDps", ref _getOptimalDps);
			AssignMethod(delegates, "GetActiveAmmo", ref _getActiveAmmo);
			AssignMethod(delegates, "SetActiveAmmo", ref _setActiveAmmo);
			AssignMethod(delegates, "MonitorProjectile", ref _monitorProjectile);
			AssignMethod(delegates, "UnMonitorProjectile", ref _unMonitorProjectile);
			AssignMethod(delegates, "GetProjectileState", ref _getProjectileState);
			AssignMethod(delegates, "GetConstructEffectiveDps", ref _getConstructEffectiveDps);
			AssignMethod(delegates, "GetPlayerController", ref _getPlayerController);
			AssignMethod(delegates, "GetWeaponAzimuthMatrix", ref _getWeaponAzimuthMatrix);
			AssignMethod(delegates, "GetWeaponElevationMatrix", ref _getWeaponElevationMatrix);
			AssignMethod(delegates, "IsTargetValid", ref _isTargetValid);
			AssignMethod(delegates, "GetWeaponScope", ref _getWeaponScope);
			AssignMethod(delegates, "IsInRange", ref _isInRange);

			if (getWeaponDefinitions)
			{
				var byteArrays = new List<byte[]>();
				GetAllWeaponDefinitions(byteArrays);
				foreach (var byteArray in byteArrays)
					WeaponDefinitions.Add(MyAPIGateway.Utilities.SerializeFromBinary<WcApiDef.WeaponDefinition>(byteArray));
			}
		}

		private void AssignMethod<T>(IReadOnlyDictionary<string, Delegate> delegates, string name, ref T field)
			where T : class
		{
			if (delegates == null)
			{
				field = null;
				return;
			}

			Delegate del;
			if (!delegates.TryGetValue(name, out del))
				throw new Exception($"{GetType().Name} :: Couldn't find {name} delegate of type {typeof(T)}");

			field = del as T;

			if (field == null)
				throw new Exception(
					$"{GetType().Name} :: Delegate {name} is not type {typeof(T)}, instead it's: {del.GetType()}");
		}

		public void GetAllWeaponDefinitions(IList<byte[]> collection) => _getAllWeaponDefinitions?.Invoke(collection);
		public void GetAllCoreWeapons(ICollection<MyDefinitionId> collection) => _getCoreWeapons?.Invoke(collection);

		public void GetAllCoreStaticLaunchers(ICollection<MyDefinitionId> collection) =>
			_getCoreStaticLaunchers?.Invoke(collection);

		public void GetAllCoreTurrets(ICollection<MyDefinitionId> collection) => _getCoreTurrets?.Invoke(collection);

		public bool GetBlockWeaponMap(IMyTerminalBlock weaponBlock, IDictionary<string, int> collection) =>
			_getBlockWeaponMap?.Invoke(weaponBlock, collection) ?? false;

		public MyTuple<bool, int, int> GetProjectilesLockedOn(IMyEntity victim) =>
			_getProjectilesLockedOn?.Invoke(victim) ?? new MyTuple<bool, int, int>();

		public void GetSortedThreats(IMyEntity shooter, ICollection<MyTuple<IMyEntity, float>> collection) =>
			_getSortedThreats?.Invoke(shooter, collection);

		public IMyEntity GetAiFocus(IMyEntity shooter, int priority = 0) => _getAiFocus?.Invoke(shooter, priority);

		public bool SetAiFocus(IMyEntity shooter, IMyEntity target, int priority = 0) =>
			_setAiFocus?.Invoke(shooter, target, priority) ?? false;

		public MyTuple<bool, bool, bool, IMyEntity> GetWeaponTarget(IMyTerminalBlock weapon, int weaponId = 0) =>
			_getWeaponTarget?.Invoke(weapon, weaponId) ?? new MyTuple<bool, bool, bool, IMyEntity>();

		public void SetWeaponTarget(IMyTerminalBlock weapon, IMyEntity target, int weaponId = 0) =>
			_setWeaponTarget?.Invoke(weapon, target, weaponId);

		public void FireWeaponOnce(IMyTerminalBlock weapon, bool allWeapons = true, int weaponId = 0) =>
			_fireWeaponOnce?.Invoke(weapon, allWeapons, weaponId);

		public void ToggleWeaponFire(IMyTerminalBlock weapon, bool on, bool allWeapons, int weaponId = 0) =>
			_toggleWeaponFire?.Invoke(weapon, on, allWeapons, weaponId);

		public bool IsWeaponReadyToFire(IMyTerminalBlock weapon, int weaponId = 0, bool anyWeaponReady = true,
			bool shootReady = false) =>
			_isWeaponReadyToFire?.Invoke(weapon, weaponId, anyWeaponReady, shootReady) ?? false;

		public float GetMaxWeaponRange(IMyTerminalBlock weapon, int weaponId) =>
			_getMaxWeaponRange?.Invoke(weapon, weaponId) ?? 0f;

		public bool GetTurretTargetTypes(IMyTerminalBlock weapon, IList<string> collection, int weaponId = 0) =>
			_getTurretTargetTypes?.Invoke(weapon, collection, weaponId) ?? false;

		public void SetTurretTargetTypes(IMyTerminalBlock weapon, IList<string> collection, int weaponId = 0) =>
			_setTurretTargetTypes?.Invoke(weapon, collection, weaponId);

		public void SetBlockTrackingRange(IMyTerminalBlock weapon, float range) =>
			_setBlockTrackingRange?.Invoke(weapon, range);

		public bool IsTargetAligned(IMyTerminalBlock weapon, IMyEntity targetEnt, int weaponId) =>
			_isTargetAligned?.Invoke(weapon, targetEnt, weaponId) ?? false;

		public MyTuple<bool, Vector3D?> IsTargetAlignedExtended(IMyTerminalBlock weapon, IMyEntity targetEnt, int weaponId) =>
			_isTargetAlignedExtended?.Invoke(weapon, targetEnt, weaponId) ?? new MyTuple<bool, Vector3D?>();

		public bool CanShootTarget(IMyTerminalBlock weapon, IMyEntity targetEnt, int weaponId) =>
			_canShootTarget?.Invoke(weapon, targetEnt, weaponId) ?? false;

		public Vector3D? GetPredictedTargetPosition(IMyTerminalBlock weapon, IMyEntity targetEnt, int weaponId) =>
			_getPredictedTargetPos?.Invoke(weapon, targetEnt, weaponId) ?? null;

		public float GetHeatLevel(IMyTerminalBlock weapon) => _getHeatLevel?.Invoke(weapon) ?? 0f;
		public float GetCurrentPower(IMyTerminalBlock weapon) => _currentPowerConsumption?.Invoke(weapon) ?? 0f;
		public float GetMaxPower(MyDefinitionId weaponDef) => _getMaxPower?.Invoke(weaponDef) ?? 0f;
		public void DisableRequiredPower(IMyTerminalBlock weapon) => _disableRequiredPower?.Invoke(weapon);
		public bool HasGridAi(IMyEntity entity) => _hasGridAi?.Invoke(entity) ?? false;
		public bool HasCoreWeapon(IMyTerminalBlock weapon) => _hasCoreWeapon?.Invoke(weapon) ?? false;
		public float GetOptimalDps(IMyEntity entity) => _getOptimalDps?.Invoke(entity) ?? 0f;

		public string GetActiveAmmo(IMyTerminalBlock weapon, int weaponId) =>
			_getActiveAmmo?.Invoke(weapon, weaponId) ?? null;

		public void SetActiveAmmo(IMyTerminalBlock weapon, int weaponId, string ammoType) =>
			_setActiveAmmo?.Invoke(weapon, weaponId, ammoType);

		public void MonitorProjectileCallback(IMyTerminalBlock weapon, int weaponId, Action<long, int, ulong, long, Vector3D, bool> action) =>
			_monitorProjectile?.Invoke(weapon, weaponId, action);

		public void UnMonitorProjectileCallback(IMyTerminalBlock weapon, int weaponId, Action<long, int, ulong, long, Vector3D, bool> action) =>
			_unMonitorProjectile?.Invoke(weapon, weaponId, action);

		public MyTuple<Vector3D, Vector3D, float, float, long, string> GetProjectileState(ulong projectileId) =>
			_getProjectileState?.Invoke(projectileId) ?? new MyTuple<Vector3D, Vector3D, float, float, long, string>();

		public float GetConstructEffectiveDps(IMyEntity entity) => _getConstructEffectiveDps?.Invoke(entity) ?? 0f;

		public long GetPlayerController(IMyTerminalBlock weapon) => _getPlayerController?.Invoke(weapon) ?? -1;

		public Matrix GetWeaponAzimuthMatrix(IMyTerminalBlock weapon, int weaponId) =>
			_getWeaponAzimuthMatrix?.Invoke(weapon, weaponId) ?? Matrix.Zero;

		public Matrix GetWeaponElevationMatrix(IMyTerminalBlock weapon, int weaponId) =>
			_getWeaponElevationMatrix?.Invoke(weapon, weaponId) ?? Matrix.Zero;

		public bool IsTargetValid(IMyTerminalBlock weapon, IMyEntity target, bool onlyThreats, bool checkRelations) =>
			_isTargetValid?.Invoke(weapon, target, onlyThreats, checkRelations) ?? false;

		public MyTuple<Vector3D, Vector3D> GetWeaponScope(IMyTerminalBlock weapon, int weaponId) =>
			_getWeaponScope?.Invoke(weapon, weaponId) ?? new MyTuple<Vector3D, Vector3D>();

		// block/grid, Threat, Other 
		public MyTuple<bool, bool> IsInRange(IMyEntity entity) =>
			_isInRange?.Invoke(entity) ?? new MyTuple<bool, bool>();
	}
}
