#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using XRL.World;
using XRL.World.Parts;

namespace RemoveItemMods
{
	internal static class Constants
	{
		public const string BUILD = "Build";

		public const string CONTEXT = "Tinkering";

		public const string MOD = "Mod";

		public const string UNMOD = "Un-Mod";

		public const int VIEWPORT_SIZE = 12;
	}

	internal static class GeneralExt
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
