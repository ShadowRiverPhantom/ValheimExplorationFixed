// Reference source for the fixes that src\fix-exploration.ps1 applies to the released plugin.
//
// Exploration is by blaxxun - https://github.com/blaxxun-boop/Exploration (master, 16d5fc7).
//
// This file is NOT compiled by build.ps1. The released Exploration 1.0.4 plugin is repaired at IL
// level, and this is the same three fixes written out at source level so that they can be read.
// Each one is marked with [FIX n], matching the numbering used by the script.
//
// Note: upstream master still declares ModVersion "1.0.3" while the released 1.0.4 DLL reports
//       1.0.4. The repaired plugin keeps 1.0.4, because ServerSync refuses clients whose version
//       does not match.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using JetBrains.Annotations;
using ServerSync;
using SkillManager;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Exploration;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInIncompatibility("org.bepinex.plugins.valheim_plus")]
public class Exploration : BaseUnityPlugin
{
	private const string ModName = "Exploration";
	private const string ModVersion = "1.0.4";
	private const string ModGUID = "org.bepinex.plugins.exploration";

	private static readonly ConfigSync configSync = new(ModName) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };

	private static ConfigEntry<Toggle> serverConfigLocked = null!;
	private static ConfigEntry<int> explorationRadiusIncrease = null!;
	private static ConfigEntry<int> movementSpeedIncrease = null!;
	private static ConfigEntry<int> wishboneRadiusIncrease = null!;
	private static ConfigEntry<int> requiredLevelWrite = null!;
	private static ConfigEntry<int> requiredLevelRead = null!;
	private static ConfigEntry<int> treasureMultiplyLevel = null!;
	private static ConfigEntry<int> treasureMultiplyChance = null!;
	private static ConfigEntry<float> experienceGainedFactor = null!;
	private static ConfigEntry<int> experienceLoss = null!;

	private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
	{
		ConfigEntry<T> configEntry = Config.Bind(group, name, value, description);

		SyncedConfigEntry<T> syncedConfigEntry = configSync.AddConfigEntry(configEntry);
		syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

		return configEntry;
	}

	private ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true) => config(group, name, value, new ConfigDescription(description), synchronizedSetting);

	private enum Toggle
	{
		On = 1,
		Off = 0,
	}

	private class ConfigurationManagerAttributes
	{
		[UsedImplicitly] public int? Order;
		[UsedImplicitly] public bool? ShowRangeAsPercent;
	}

	private static Skill exploration = null!;

	public void Awake()
	{
		exploration = new Skill("Exploration", "exploration.png");
		exploration.Description.English("Increases movement speed and exploration radius.");
		exploration.Name.German("Erkundung");
		exploration.Description.German("Erhöht die Bewegungsgeschwindigkeit und den Erkundungsradius.");
		exploration.Configurable = false;

		int order = 0;

		serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.");
		configSync.AddLockingConfigEntry(serverConfigLocked);
		explorationRadiusIncrease = config("2 - Exploration", "Exploration Radius Increase", 250, new ConfigDescription("Exploration radius increase at skill level 100.", new AcceptableValueRange<int>(0, 1000), new ConfigurationManagerAttributes { Order = --order }));
		movementSpeedIncrease = config("2 - Exploration", "Movement Speed Increase", 15, new ConfigDescription("Movement speed increase at skill level 100.", new AcceptableValueRange<int>(0, 30), new ConfigurationManagerAttributes { Order = --order }));
		wishboneRadiusIncrease = config("2 - Exploration", "Wishbone Radius Increase", 50, new ConfigDescription("Wishbone radius increase at skill level 100.", new AcceptableValueRange<int>(0, 100), new ConfigurationManagerAttributes { Order = --order }));
		requiredLevelWrite = config("2 - Exploration", "Cartography Write Level", 20, new ConfigDescription("Exploration skill level required to write your map to the cartography table. Set to 0 to disable the requirement.", new AcceptableValueRange<int>(0, 100), new ConfigurationManagerAttributes { Order = --order, ShowRangeAsPercent = false }));
		requiredLevelRead = config("2 - Exploration", "Cartography Read Level", 40, new ConfigDescription("Exploration skill level required to read the map from the cartography table. Set to 0 to disable the requirement.", new AcceptableValueRange<int>(0, 100), new ConfigurationManagerAttributes { Order = --order, ShowRangeAsPercent = false }));
		treasureMultiplyLevel = config("2 - Exploration", "Treasure Multiplication Level", 50, new ConfigDescription("Exploration skill level required to have a chance to multiply the content of treasure chests. Set to 0 to disable this.", new AcceptableValueRange<int>(0, 100), new ConfigurationManagerAttributes { Order = --order, ShowRangeAsPercent = false }));
		treasureMultiplyChance = config("2 - Exploration", "Treasure Multiplication Chance", 25, new ConfigDescription("Chance to multiply the content of treasure chests, if the required skill level is reached.", new AcceptableValueRange<int>(0, 100), new ConfigurationManagerAttributes { Order = --order }));
		experienceGainedFactor = config("3 - Other", "Skill Experience Gain Factor", 1f, new ConfigDescription("Factor for experience gained for the exploration skill.", new AcceptableValueRange<float>(0.01f, 5f), new ConfigurationManagerAttributes { Order = --order }));
		experienceGainedFactor.SettingChanged += (_, _) => exploration.SkillGainFactor = experienceGainedFactor.Value;
		exploration.SkillGainFactor = experienceGainedFactor.Value;
		experienceLoss = config("3 - Other", "Skill Experience Loss", 0, new ConfigDescription("How much experience to lose in the exploration skill on death.", new AcceptableValueRange<int>(0, 100), new ConfigurationManagerAttributes { Order = --order }));
		experienceLoss.SettingChanged += (_, _) => exploration.SkillLoss = experienceLoss.Value;
		exploration.SkillLoss = experienceLoss.Value;

		Assembly assembly = Assembly.GetExecutingAssembly();
		Harmony harmony = new(ModGUID);
		harmony.PatchAll(assembly);
	}

	[HarmonyPatch(typeof(Minimap), nameof(Minimap.Explore), typeof(Vector3), typeof(float))]
	private class IncreaseExplorationRadius
	{
		public static int exploredPixels = 0;

		[UsedImplicitly]
		private static void Prefix(Minimap __instance, ref float radius)
		{
			// [FIX 3] A dedicated server has no local player; the original throws NullReferenceException here.
			if (!Player.m_localPlayer) return;
			exploredPixels = 0;
			radius *= 1 + Player.m_localPlayer.GetSkillFactor("Exploration") * (explorationRadiusIncrease.Value / 100f);
		}

		private static void Postfix()
		{
			if (exploredPixels > 0 && Player.m_localPlayer)
			{
				Player.m_localPlayer.RaiseSkill("Exploration", 0.075f * exploredPixels);
			}
		}
	}

	[HarmonyPatch(typeof(Minimap), nameof(Minimap.Explore), typeof(int), typeof(int))]
	private static class IncreaseExplorationSkill
	{
		private static void Postfix(bool __result)
		{
			if (__result)
			{
				++IncreaseExplorationRadius.exploredPixels;
			}
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.GetJogSpeedFactor))]
	private class IncreaseJogSpeed
	{
		private static void Postfix(Player __instance, ref float __result)
		{
			__result += __instance.GetSkillFactor("Exploration") * (movementSpeedIncrease.Value / 100f);
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.GetRunSpeedFactor))]
	private class IncreaseRunSpeed
	{
		private static void Postfix(Player __instance, ref float __result)
		{
			__result += __instance.GetSkillFactor("Exploration") * (movementSpeedIncrease.Value / 100f);
		}
	}


	[HarmonyPatch(typeof(MapTable), nameof(MapTable.OnWrite))]
	private static class PreventMapTableUsageWrite
	{
		// [FIX 3] Null check added; without a local player the check is skipped instead of throwing.
		private static bool Prefix()
		{
			if (!Player.m_localPlayer) return true;
			return requiredLevelWrite.Value <= 0 || !(Player.m_localPlayer.GetSkillFactor("Exploration") < requiredLevelWrite.Value / 100f);
		}
	}

	[HarmonyPatch(typeof(MapTable), nameof(MapTable.OnRead), typeof(Switch), typeof(Humanoid), typeof(ItemDrop.ItemData), typeof(bool))]
	private static class PreventMapTableUsageRead
	{
		// [FIX 3] Null check added.
		private static bool Prefix()
		{
			if (!Player.m_localPlayer) return true;
			return requiredLevelRead.Value <= 0 || !(Player.m_localPlayer.GetSkillFactor("Exploration") < requiredLevelRead.Value / 100f);
		}
	}

	[HarmonyPatch(typeof(MapTable), nameof(MapTable.GetWriteHoverText))]
	private static class UpdateHoverTextWrite
	{
		private static void Postfix(MapTable __instance, ref string __result)
		{
			// [FIX 3] Null check added.
			if (!Player.m_localPlayer) return;
			if (requiredLevelWrite.Value > 0 && Player.m_localPlayer.GetSkillFactor("Exploration") < requiredLevelWrite.Value / 100f)
			{
				__result = Localization.instance.Localize(__instance.m_name + $"\nRequires Exploration level {requiredLevelWrite.Value}");
			}
		}
	}

	[HarmonyPatch(typeof(MapTable), nameof(MapTable.GetReadHoverText))]
	private static class UpdateHoverTextRead
	{
		private static void Postfix(MapTable __instance, ref string __result)
		{
			// [FIX 3] Null check added.
			if (!Player.m_localPlayer) return;
			if (requiredLevelRead.Value > 0 && Player.m_localPlayer.GetSkillFactor("Exploration") < requiredLevelRead.Value / 100f)
			{
				__result = Localization.instance.Localize(__instance.m_name + $"\nRequires Exploration level {requiredLevelRead.Value}");
			}
		}
	}

	// [FIX 1] The original patches nameof(Container.RPC_OpenRespons), a method that no longer
	//         exists: current Valheim calls it RPC_OpenResponse (same parameters, long sender and
	//         bool granted). A missing target makes Harmony.PatchAll throw, and because HarmonyX
	//         does not catch per patch, every patch after this one is skipped - including
	//         SkillManager's IsSkillValid, which is why custom skill levels reset on load.
	[HarmonyPatch(typeof(Container), nameof(Container.RPC_OpenResponse))]
	private static class MultiplyTreasure
	{
		private static void Prefix(Container __instance, bool granted)
		{
			if (!Player.m_localPlayer || !granted || !__instance.name.StartsWith("TreasureChest_", StringComparison.Ordinal))
			{
				return;
			}

			if (__instance.m_nview.GetZDO().GetBool("Exploration Treasure Looted"))
			{
				return;
			}

			__instance.m_nview.GetZDO().Set("Exploration Treasure Looted", true);

			// [FIX 2] The original reads treasureMultiplyLevel.Value / 100f >= GetSkillFactor(...),
			//         which triggers when the skill is *below* the threshold - the opposite of what
			//         the setting says.
			if (Player.m_localPlayer.GetSkillFactor("Exploration") >= treasureMultiplyLevel.Value / 100f && Random.value < treasureMultiplyChance.Value / 100f)
			{
				Inventory inventory = __instance.GetInventory();
				foreach (ItemDrop.ItemData item in inventory.GetAllItems().ToArray())
				{
					__instance.m_inventory.AddItem(item.m_dropPrefab, item.m_stack);
				}
			}

			Player.m_localPlayer.RaiseSkill("Exploration", 35f);
		}
	}

	[HarmonyPatch]
	private static class AlterBeaconRange
	{
		private static IEnumerable<MethodInfo> TargetMethods() => new[]
		{
			AccessTools.DeclaredMethod(typeof(SE_Finder), nameof(SE_Finder.UpdateStatusEffect)),
			AccessTools.DeclaredMethod(typeof(Beacon), nameof(Beacon.FindBeaconsInRange)),
			AccessTools.DeclaredMethod(typeof(Beacon), nameof(Beacon.FindClosestBeaconInRange)),
		};

		private static readonly FieldInfo range = AccessTools.DeclaredField(typeof(Beacon), nameof(Beacon.m_range));

		// [FIX 3] This transpiler is injected into SE_Finder and Beacon, and
		//         Beacon.FindBeaconsInRange runs on the server, where Player.m_localPlayer is null.
		//         Without a local player the range is returned unchanged.
		private static float ModifyBeaconRange(float range)
		{
			if (!Player.m_localPlayer) return range;
			return range * (1 + wishboneRadiusIncrease.Value / 100f * Player.m_localPlayer.GetSkillFactor("Exploration"));
		}

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			foreach (CodeInstruction instruction in instructions)
			{
				yield return instruction;
				if (instruction.LoadsField(range))
				{
					yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(AlterBeaconRange), nameof(ModifyBeaconRange)));
				}
			}
		}
	}
}
