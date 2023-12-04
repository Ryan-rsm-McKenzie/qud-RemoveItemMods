#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using ConsoleLib.Console;
using HarmonyLib;
using XRL.UI;
using XRL.World;
using XRL.World.Anatomy;
using XRL.World.Capabilities;
using XRL.World.Parts;
using XRL.World.Tinkering;

namespace RemoveItemMods
{
	using Temporary = XRL.World.Parts.Temporary;

	internal readonly struct Tinkering
	{
		public readonly BitCost Cost;

		public readonly TinkerData Tinker;

		private readonly GameObject _item;

		private readonly IModification _modification;

		public Tinkering(GameObject item, TinkerData tinker, IModification modification, BitCost cost)
		{
			this._item = item;
			this.Tinker = tinker;
			this._modification = modification;
			this.Cost = cost;
		}

		public readonly bool OnPress(GameObject subject, BitLocker wallet)
		{
			bool success = false;
			if (!wallet.HasBits(this.Cost)) {
				subject.Fail($"You don't have the required <{this.Cost}> bits! You have:\n\n{wallet.GetBitsString()}");
			} else if (subject.AreHostilesNearby() && subject.FireEvent("CombatPreventsTinkering")) {
				subject.Fail("You can't tinker with hostiles nearby!");
			} else if (subject.CheckFrozen() && this.UnequipItemIfNeeded(subject, out var part) && this.UseIngredientsIfNeeded(subject)) {
				var item = this._item;
				try {
					wallet.UseBits(this.Cost);
					item.SplitStack(1, subject);
					item.RemovePart(this._modification);
					Popup.Show($"You un-mod {item.t(Stripped: true)} to no longer be {{{{C|{this.Tinker.DisplayName}}}}}.");
					if (item.Equipped is null && item.InInventory is null)
						subject.ReceiveObject(item, Context: Constants.CONTEXT);
					success = true;
				} catch (Exception e) {
					MetricsManager.LogError("Exception removeing mod", e);
				} finally {
					if (GameObject.Validate(ref item) && part is not null && part.Equipped is null) {
						var e = Event.New("CommandEquipObject");
						e.SetParameter("Object", item);
						e.SetParameter("BodyPart", part);
						e.SetParameter("EnergyCost", 0);
						e.SetParameter("Context", Constants.CONTEXT);
						subject.FireEvent(e);
					}
				}
			}

			return success;
		}

		private readonly bool UnequipItemIfNeeded(GameObject subject, out BodyPart? part)
		{
			part = null;
			if (this._item.Equipped == subject) {
				part = subject.FindEquippedObject(this._item);
				if (part is null) {
					MetricsManager.LogError($"could not find equipping part for {this._item.Blueprint} {this._item.DebugName} tracked as equipped");
					return false;
				}

				var e = Event.New("CommandUnequipObject");
				e.SetParameter("BodyPart", part);
				e.SetParameter("EnergyCost", 0);
				e.SetParameter("Context", Constants.CONTEXT);
				e.SetFlag("NoStack", State: true);
				if (!subject.FireEvent(e)) {
					subject.Fail($"You can't unequip {this._item.t()}.");
					return false;
				}
			}

			return true;
		}

		private readonly bool UseIngredientsIfNeeded(GameObject subject)
		{
			if (!this.Tinker.Ingredient.IsNullOrEmpty()) {
				var inventory = subject.Inventory;
				var blueprints = this.Tinker.Ingredient.CachedCommaExpansion();
				GameObject? ingredient = null;
				GameObject? temporary = null;
				foreach (string blueprint in blueprints) {
					ingredient = inventory.FindObjectByBlueprint(blueprint, Temporary.IsNotTemporary);
					if (ingredient is not null)
						break;
					temporary ??= inventory.FindObjectByBlueprint(blueprint);
				}

				if (ingredient is null) {
					if (temporary is not null) {
						string possessive = temporary.HasProperName ? "" : "Your ";
						subject.Fail($"{possessive}{temporary.ShortDisplayName}{temporary.Is} too unstable to craft with.");
					} else {
						string required = string.Concat(blueprints
							.Map(x => TinkeringHelpers.TinkeredItemShortDisplayName(x))
							.Intersperse(" or "));
						subject.Fail($"You don't have the required ingredient(s): {required}!");
					}
					return false;
				} else {
					ingredient.SplitStack(1, subject);
					if (!inventory.FireEvent(Event.New("CommandRemoveObject", "Object", ingredient))) {
						subject.Fail("You can not use the ingredient!");
						return false;
					}
				}
			}

			return true;
		}
	}

	internal readonly struct Unmoddable
	{
		public readonly Tinkering? Tinkering;

		private readonly string _display;

		public Unmoddable(string display, Tinkering? tinkering = null)
		{
			this._display = display;
			this.Tinkering = tinkering;
		}

		public readonly bool OnPress(GameObject go, BitLocker wallet) => this.Tinkering?.OnPress(go, wallet) ?? false;

		public readonly override string ToString() => this._display;
	}

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

	internal static class Constants
	{
		public const string BUILD = "Build";

		public const string CONTEXT = "Tinkering";

		public const string MOD = "Mod";

		public const string UNMOD = "Un-Mod";

		public const int VIEWPORT_SIZE = 12;
	}

	internal static class CustomTinkeringScreen
	{
		private static int s_selection = 0;

		private static Unmoddable[]? s_unmoddables = null;

		private static ArraySegment<Unmoddable> s_viewport = new();

		public static void Cleanup()
		{
			s_selection = 0;
			s_unmoddables = null;
			s_viewport = new();
		}

		public static void Input(Keys input, GameObject go, BitLocker bits)
		{
			if (s_viewport.Count > 0) {
				int max = Math.Max(s_unmoddables!.Length - 1, 0);
				if (input == Keys.NumPad8) {
					s_selection = MathExt.Clamp(s_selection - 1, 0, max);
				} else if (input == Keys.NumPad2) {
					s_selection = MathExt.Clamp(s_selection + 1, 0, max);
				} else if (Keyboard.IsCommandKey("Page Down")) {
					int last = s_viewport.Offset + s_viewport.Count - 1;
					s_selection = s_selection == last ?
						MathExt.Clamp(last + (Constants.VIEWPORT_SIZE - 1), 0, max) :
						last;
				} else if (Keyboard.IsCommandKey("Page Up")) {
					int first = s_viewport.Offset;
					s_selection = s_selection == first ?
						MathExt.Clamp(first - (Constants.VIEWPORT_SIZE - 1), 0, max) :
						first;
				} else if (input == Keys.Space || input == Keys.Enter) {
					s_unmoddables[s_selection].OnPress(go, bits);
					Invalidate();
				}
			}
		}

		public static void Invalidate() => s_unmoddables = null;

		public static void Show(
			ScreenBuffer screen,
			GameObject go,
			List<TinkerData> recipes,
			ref TinkerData? tinkering,
			BitCost cost)
		{
			screen.Goto(2, 1);
			screen.Write($"  {{{{w|{Constants.BUILD}}}}}    {{{{w|{Constants.MOD}}}}}  {{{{Y|>}}}} {{{{W|{Constants.UNMOD}}}}}");

			if (s_unmoddables is null) {
				int offset = s_viewport.Offset;
				s_unmoddables = ParseUnmoddables(go, recipes);
				int max = Math.Max(s_unmoddables.Length - 1, 0);
				s_selection = MathExt.Clamp(s_selection, 0, max);
				Reslice(MathExt.Clamp(offset, 0, max));
			}

			if (s_selection < s_viewport.Offset)
				Reslice(s_selection);
			else if (s_selection >= s_viewport.Offset + s_viewport.Count)
				Reslice(MathExt.Clamp(s_selection - (Constants.VIEWPORT_SIZE - 1), 0, Math.Max(s_unmoddables.Length - 1, 0)));

			if (!go.HasSkill("Tinkering")) {
				screen.Goto(4, 4);
				screen.Write("You don't have the Tinkering skill.");
			} else if (s_viewport.Count == 0) {
				screen.Goto(4, 4);
				screen.Write("You don't have any un-moddable items.");
			} else {
				foreach (var (i, unmoddable) in s_viewport.Enumerate()) {
					screen.Goto(4, i + 3);
					screen.Write(
						StringFormat.ClipLine(
							Input: unmoddable.ToString(),
							MaxWidth: 46
						));
					if (s_selection == i + s_viewport.Offset) {
						screen.Goto(2, i + 3);
						screen.Write("{{Y|>}}");
						if (unmoddable.Tinkering is not null) {
							var tinker = unmoddable.Tinkering.Value;
							tinkering = tinker.Tinker;
							tinker.Cost.CopyTo(cost);
							string description = ItemModding.GetModificationDescription(tinkering.Blueprint, go);
							screen.Goto(2, 18);
							screen.WriteBlockWithNewlines(StringFormat.ClipText(
								Input: $"{{{{rules|{description}}}}}",
								MaxWidth: 76,
								KeepNewlines: true));
						} else {
							tinkering = null;
							cost.Clear();
						}
					}
				}

				screen.Goto(1, 16);
				screen.Write("".PadLeft(50, '\xC4'));
			}
		}

		public static void SwitchTab(ref string tab, Keys input)
		{
			bool left = input == Keys.NumPad4;
			if (tab == Constants.BUILD)
				tab = left ? Constants.UNMOD : Constants.MOD;
			else if (tab == Constants.MOD)
				tab = left ? Constants.BUILD : Constants.UNMOD;
			else if (tab == Constants.UNMOD)
				tab = left ? Constants.MOD : Constants.BUILD;
		}

		private static Unmoddable[] ParseUnmoddables(GameObject go, List<TinkerData> recipes)
		{
			var recipesLookup = recipes
				.Map(x => (Key: x.PartName, Value: x))
				.ToDictionary(x => x.Key, x => x.Value);
			return go
				.Inventory
				.GetObjects()
				.Chain(go.Body.GetEquippedObjects())
				.FilterMap(item => {
					var mods = item
						.GetModifications()
						.FilterMap(x => {
							return recipesLookup.TryGetValue(x.Name, out var tinker) ?
								Iter.Nullable((Mod: x, Tinker: tinker)) :
								null;
						})
						.ToList();
					if (item.Understood() && mods.Count > 0) {
						return Iter
							.Once(new Unmoddable(item.DisplayName))
							.Chain(mods.Map(x => {
								var cost = new BitCost();
								Action<int> increment = x => cost.Increment(BitType.TierBits[Tier.Constrain(x)]);
								increment(x.Tinker.Tier);
								int slots = Math.Max(item.GetModificationSlotsUsed() - x.Mod.GetModificationSlotUsage(), 0);
								increment(slots - item.GetIntProperty("NoCostMods") + item.GetTechTier());
								string display = $"  {x.Tinker.DisplayName} <{cost}>";
								return new Unmoddable(display, new Tinkering(item, x.Tinker, x.Mod, cost));
							}));
					} else {
						return null;
					}
				})
				.Flatten()
				.ToArray();
		}

		private static void Reslice(int offset)
		{
			var array = s_unmoddables!;
			s_viewport = array.Slice(offset, Math.Min(Constants.VIEWPORT_SIZE, array.Length - offset));
		}
	}

	internal static class Extensions
	{
		public static List<T> Clone<T>(this List<T> self) => new(self);

		public static IEnumerable<IModification> GetModifications(this GameObject self)
		{
			return self
				.PartsList
				.FilterMap(x => x as IModification);
		}
	}

	internal static class IEnumerableExt
	{
		public static IEnumerable<T> Chain<T>(this IEnumerable<T> self, IEnumerable<T> other)
		{
			return self.Concat(other);
		}

		public static IEnumerable<(int Enumerator, T Item)> Enumerate<T>(this IEnumerable<T> self)
		{
			int i = 0;
			foreach (var elem in self) {
				yield return (i++, elem);
			}
		}

		public static IEnumerable<U> FilterMap<T, U>(this IEnumerable<T> self, Func<T, U?> f, U? _ = null)
			where U : class?
		{
			foreach (var elem in self) {
				var result = f(elem);
				if (result is not null) {
					yield return result;
				}
			}
		}

		public static IEnumerable<U> FilterMap<T, U>(this IEnumerable<T> self, Func<T, U?> f, U? _ = null)
			where U : struct
		{
			foreach (var elem in self) {
				var result = f(elem);
				if (result.HasValue) {
					yield return result.Value;
				}
			}
		}

		public static IEnumerable<T> Flatten<T>(this IEnumerable<IEnumerable<T>> self)
		{
			foreach (var outer in self) {
				foreach (var inner in outer) {
					yield return inner;
				}
			}
		}

		public static IEnumerable<T> Intersperse<T>(this IEnumerable<T> self, T separator)
		{
			var iter = self.GetEnumerator();
			if (iter.MoveNext()) {
				var current = iter.Current;
				while (iter.MoveNext()) {
					yield return current;
					yield return separator;
					current = iter.Current;
				}
				yield return current;
			}
		}

		public static IEnumerable<U> Map<T, U>(this IEnumerable<T> self, Func<T, U> f)
		{
			return self.Select(f);
		}

		public static ArraySegment<T> Slice<T>(this T[] self, int offset, int count)
		{
			return new ArraySegment<T>(self, offset, count);
		}
	}

	internal static class Iter
	{
#pragma warning disable IDE0001 // Simplify Names
		public static Nullable<T> Nullable<T>(T value)
			where T : struct
		{
			return new Nullable<T>(value);
		}

#pragma warning restore IDE0001

		public static IEnumerable<T> Once<T>(T value)
		{
			yield return value;
		}
	}

	internal static class MathExt
	{
		public static T Clamp<T>(T v, T lo, T hi)
			where T : IComparable<T>
		{
			return v.CompareTo(lo) < 0 ? lo : v.CompareTo(hi) > 0 ? hi : v;
		}
	}
}
