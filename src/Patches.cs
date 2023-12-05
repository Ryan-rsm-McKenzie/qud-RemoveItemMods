#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using ConsoleLib.Console;
using HarmonyLib;
using XRL.UI;
using XRL.World;
using XRL.World.Parts;
using XRL.World.Tinkering;

namespace RemoveItemMods
{
	[HarmonyPatch(typeof(TinkeringScreen))]
	[HarmonyPatch(nameof(TinkeringScreen.Show))]
	[HarmonyPatch(new Type[] { typeof(GameObject), typeof(GameObject), typeof(IEvent) })]
	public class TinkeringScreen_Show
	{
		public static void Postfix() => CustomTinkeringScreen.Cleanup();

		public static IEnumerable<CodeInstruction> Transpiler(
			IEnumerable<CodeInstruction> instructions,
			ILGenerator generator)
		{
			var working = instructions.ToArray();

			var header = GetHeaderStringVariable(working);
			if (header is null) {
				Logger.buildLog.Error("Failed to locate header string local variable!");
				return instructions;
			}

			if (!PatchHeaderStrings(ref working, header)) {
				Logger.buildLog.Error("Failed to patch header strings!");
				return instructions;
			}

			if (!PatchInputLoop(ref working, header, generator)) {
				Logger.buildLog.Error("Failed to patch input loop!");
				return instructions;
			}

			if (!PatchItemListInvalidation(ref working)) {
				Logger.buildLog.Error("Failed to patch item list invalidation!");
				return instructions;
			}

			if (!PatchTabDrawing(ref working, header, generator)) {
				Logger.buildLog.Error("Failed to patch tab drawing!");
				return instructions;
			}

			if (!PatchTabSwitching(ref working, header)) {
				Logger.buildLog.Error("Failed to patch tab switching!");
				return instructions;
			}

			return working;
		}

		private static LocalBuilder? GetHeaderStringVariable(CodeInstruction[] instructions)
		{
			var matcher = new CodeMatcher(instructions)
				.MatchStartForward(new CodeMatch[] {
					new(OpCodes.Ldloc_S),
					new(OpCodes.Ldstr, Constants.BUILD),
					new(OpCodes.Call, AccessTools.Method(
						type: typeof(string),
						name: "op_Equality",
						parameters: new Type[] { typeof(string), typeof(string) }
					)),
					new(OpCodes.Brfalse),
				});
			return matcher.IsValid ? matcher.Operand as LocalBuilder : null;
		}

		private static bool PatchHeaderStrings(
			ref CodeInstruction[] instructions,
			LocalBuilder header)
		{
			var matcher = new CodeMatcher(instructions);

			matcher
				.Start()
				.MatchStartForward(new CodeMatch[] {
					new(OpCodes.Ldstr, $"{{{{Y|>}}}} {{{{W|{Constants.BUILD}}}}}    {{{{w|{Constants.MOD}}}}}"),
				});
			if (matcher.IsInvalid)
				return false;
			matcher.Operand = $"{{{{Y|>}}}} {{{{W|{Constants.BUILD}}}}}    {{{{w|{Constants.MOD}}}}}    {{{{w|{Constants.UNMOD}}}}}";

			matcher
				.Start()
				.MatchStartForward(new CodeMatch[] {
					new(OpCodes.Ldstr, $"  {{{{w|{Constants.BUILD}}}}}  {{{{Y|>}}}} {{{{W|{Constants.MOD}}}}}"),
				});
			if (matcher.IsInvalid)
				return false;
			matcher.Operand = $"  {{{{w|{Constants.BUILD}}}}}  {{{{Y|>}}}} {{{{W|{Constants.MOD}}}}}    {{{{w|{Constants.UNMOD}}}}}";

			matcher
				.Start()
				.MatchStartForward(new CodeMatch[] {
					new(OpCodes.Ldloc_S, header),
					new(OpCodes.Ldstr, Constants.BUILD),
					new(OpCodes.Call, AccessTools.Method(
						type: typeof(string),
						name: "op_Equality",
						parameters: new Type[] { typeof(string), typeof(string) }
					)),
					new(OpCodes.Brfalse_S),
				})
				.MatchStartForward(new CodeMatch[] {
					new(OpCodes.Br_S),
					new(OpCodes.Ldloc_1),
					new(OpCodes.Ldc_I4_2),
					new(OpCodes.Ldc_I4_1),
					new(OpCodes.Callvirt, AccessTools.Method(
						type: typeof(ScreenBuffer),
						name: nameof(ScreenBuffer.Goto),
						parameters: new Type[] { typeof(int), typeof(int) }
					)),
					new(OpCodes.Pop),
				});
			if (matcher.IsInvalid)
				return false;
			object exit = matcher.Operand;
			matcher.Advance(1);
			var pushHeader = new CodeInstruction(OpCodes.Ldloc_S, header) {
				labels = matcher.Labels,
			};
			matcher.Labels = new();

			instructions = matcher
				.Insert(new CodeInstruction[] {
					pushHeader,
					new(OpCodes.Ldstr, Constants.MOD),
					new(OpCodes.Call, AccessTools.Method(
						type: typeof(string),
						name: "op_Equality",
						parameters: new Type[] { typeof(string), typeof(string) }
					)),
					new(OpCodes.Brfalse_S, exit),
				})
				.Instructions()
				.ToArray();
			return true;
		}

		private static bool PatchInputLoop(
			ref CodeInstruction[] instructions,
			LocalBuilder header,
			ILGenerator generator)
		{
			var matcher = new CodeMatcher(instructions);

			matcher
				.Start()
				.MatchStartForward(new CodeMatch[] {
					new(OpCodes.Ldloc_S),
					new(OpCodes.Ldloc_S),
					new(OpCodes.Callvirt, AccessTools.Method(
						type: typeof(BitLocker),
						name: nameof(BitLocker.GetBitCount),
						parameters: new Type[] { typeof(char) }
					)),
				});
			if (matcher.IsInvalid)
				return false;
			var pushBits = matcher.Instruction;

			matcher
				.Start()
				.MatchEndForward(new CodeMatch[] {
					new(OpCodes.Ldc_I4_0),
					new(OpCodes.Ldc_I4_1),
					new(OpCodes.Call, AccessTools.Method(
						type: typeof(Keyboard),
						name: nameof(Keyboard.getvk),
						parameters: new Type[] { typeof(bool), typeof(bool), typeof(bool), }
					)),
					new(OpCodes.Stloc_2),
				});
			if (matcher.IsInvalid)
				return false;
			matcher.Advance(1);

			var label = generator.DefineLabel();
			var brfalse = new CodeInstruction(OpCodes.Brfalse, label);
			matcher.Labels.Add(label);
			instructions = matcher
				.Insert(new CodeInstruction[] {
					new(OpCodes.Ldloc_S, header),
					new(OpCodes.Ldstr, Constants.UNMOD),
					new(OpCodes.Call, AccessTools.Method(
						type: typeof(string),
						name: "op_Equality",
						parameters: new Type[] { typeof(string), typeof(string) }
					)),
					brfalse,
					new(OpCodes.Ldloc_2),	// Keys
					new(OpCodes.Ldarg_1),	// GameObject
					pushBits,
					new(OpCodes.Call, AccessTools.Method(
						type: typeof(CustomTinkeringScreen),
						name: nameof(CustomTinkeringScreen.Input)
					)),
				})
				.Instructions()
				.ToArray();
			return true;
		}

		private static bool PatchItemListInvalidation(ref CodeInstruction[] instructions)
		{
			var matcher = new CodeMatcher(instructions);

			matcher
				.Start()
				.MatchEndForward(new CodeMatch[] {
					new(OpCodes.Ldc_I4_1),
					new(OpCodes.Ldc_I4_1),
					new(OpCodes.Ldc_I4_1),
					new(OpCodes.Ldc_I4_1),
					new(OpCodes.Call, AccessTools.Method(
						type: typeof(Popup),
						name: nameof(Popup.Show),
						parameters: new Type[] { typeof(string), typeof(bool), typeof(bool), typeof(bool), typeof(bool) }
					)),

					new(OpCodes.Ldloc_0),
					new(OpCodes.Ldnull),
					new(OpCodes.Stfld),
				});
			if (matcher.IsInvalid)
				return false;

			instructions = matcher
				.Advance(1)
				.Insert(new CodeInstruction[] {
					new(OpCodes.Call, AccessTools.Method(
						type: typeof(CustomTinkeringScreen),
						name: nameof(CustomTinkeringScreen.Invalidate)
					)),
				})
				.Instructions()
				.ToArray();
			return true;
		}

		private static bool PatchTabDrawing(
			ref CodeInstruction[] instructions,
			LocalBuilder header,
			ILGenerator generator)
		{
			var matcher = new CodeMatcher(instructions);

			matcher.MatchEndForward(new CodeMatch[] {
				new(OpCodes.Ldloc_S, header),
				new(OpCodes.Ldstr, Constants.MOD),
				new(OpCodes.Call, AccessTools.Method(
					type: typeof(string),
					name: "op_Equality",
					parameters: new Type[] { typeof(string), typeof(string) }
				)),
				new(OpCodes.Brfalse),
			});
			if (matcher.IsInvalid)
				return false;
			var load = new CodeInstruction(OpCodes.Ldloc_S, header) {
				labels = new() { generator.DefineLabel() },
			};
			var brfalse = new CodeInstruction(OpCodes.Brfalse, matcher.Operand);
			matcher.Operand = load.labels[0];

			var buildBranch = new CodeMatch[] {
				new(OpCodes.Br),
				new(OpCodes.Ldloc_S, header),
				new(OpCodes.Ldstr, Constants.BUILD),
				new(OpCodes.Call, AccessTools.Method(
					type: typeof(string),
					name: "op_Equality",
					parameters: new Type[] { typeof(string), typeof(string) }
				)),
				new(OpCodes.Brfalse),
			};
			matcher.MatchStartForward(buildBranch);
			if (matcher.IsInvalid)
				return false;
			var br = new CodeInstruction(OpCodes.Br, matcher.Operand);

			matcher
				.Start()
				.MatchStartForward(new CodeMatch[] {
					new(OpCodes.Ldloc_0),
					new(OpCodes.Ldfld),
					new(OpCodes.Ldloc_S),
					new(OpCodes.Callvirt, AccessTools.Method(
						type: typeof(List<TinkerData>),
						name: "Add",
						parameters: new Type[] { typeof(TinkerData) }
					)),
				});
			if (matcher.IsInvalid)
				return false;
			var pushRecipes = new CodeInstruction[] {
				instructions[matcher.Pos],
				instructions[matcher.Pos + 1],
			};

			matcher
				.Start()
				.MatchStartForward(new CodeMatch[] {
					new(OpCodes.Ldloc_S),
					new(OpCodes.Ldfld, AccessTools.Field(
						type: typeof(TinkerData),
						name: nameof(TinkerData.Ingredient)
					)),
				});
			if (matcher.IsInvalid)
				return false;
			var pushTinkering = new CodeInstruction(OpCodes.Ldloca_S, matcher.Operand);

			matcher
				.Start()
				.MatchStartForward(new CodeMatch[] {
					new(OpCodes.Ldloc_S),
					new(OpCodes.Ldloc_S),
					new(OpCodes.Callvirt, AccessTools.Method(
						type: typeof(BitCost),
						name: nameof(BitCost.CopyTo),
						parameters: new Type[] { typeof(BitCost) }
					)),
				});
			if (matcher.IsInvalid)
				return false;
			var pushCost = matcher.Advance(1).Instruction;

			instructions = matcher
				.Start()
				.MatchStartForward(buildBranch)
				.Advance(1)
				.Insert(new CodeInstruction[] {
					load,
					new(OpCodes.Ldstr, Constants.UNMOD),
					new(OpCodes.Call, AccessTools.Method(
						type: typeof(string),
						name: "op_Equality",
						parameters: new Type[] { typeof(string), typeof(string) }
					)),
					brfalse,
					new(OpCodes.Ldloc_1),	// ScreenBuffer
					new(OpCodes.Ldarg_1),	// GameObject
					pushRecipes[0],
					pushRecipes[1],
					pushTinkering,
					pushCost,
					new(OpCodes.Call, AccessTools.Method(
						type: typeof(CustomTinkeringScreen),
						name: nameof(CustomTinkeringScreen.Show)
					)),
					br,
				})
				.Instructions()
				.ToArray();
			return true;
		}

		private static bool PatchTabSwitching(
			ref CodeInstruction[] instructions,
			LocalBuilder header)
		{
			var matcher = new CodeMatcher(instructions);

			matcher.MatchEndForward(new CodeMatch[] {
				new(OpCodes.Ldloc_S),
				new(OpCodes.Brtrue_S),
				new(OpCodes.Ldloc_2),
				new(OpCodes.Ldc_I4_S, (sbyte)Keys.NumPad4),
				new(OpCodes.Beq_S),
				new(OpCodes.Ldloc_2),
				new(OpCodes.Ldc_I4_S, (sbyte)Keys.NumPad6),
				new(OpCodes.Bne_Un_S),
			});
			if (matcher.IsInvalid)
				return false;
			matcher.Advance(1);
			int start = matcher.Pos;
			var pushHeader = new CodeInstruction(OpCodes.Ldloca_S, header) {
				labels = matcher.Labels.Clone(),
			};

			matcher.MatchStartForward(new CodeMatch[] {
				new(OpCodes.Ldloc_2),
				new(OpCodes.Ldc_I4_S, (sbyte)Keys.L),
				new(OpCodes.Beq_S),
				new(OpCodes.Ldloc_2),
				new(OpCodes.Ldc_I4, (int)Keys.MouseEvent),
				new(OpCodes.Bne_Un),
			});
			if (matcher.IsInvalid)
				return false;
			int stop = matcher.Pos;

			instructions = matcher
				.Start()
				.Advance(start)
				.RemoveInstructions(stop - start)
				.Insert(new CodeInstruction[] {
					pushHeader,
					new(OpCodes.Ldloc_2),
					new(OpCodes.Call, AccessTools.Method(
						type: typeof(CustomTinkeringScreen),
						name: nameof(CustomTinkeringScreen.SwitchTab)
					)),
				})
				.Instructions()
				.ToArray();
			return true;
		}
	}
}
