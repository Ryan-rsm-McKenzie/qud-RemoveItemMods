#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using ConsoleLib.Console;
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
}
