using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace Codebase;

public static class SystemFunc
{
	private static readonly Dictionary<(Type, string), FieldInfo> FieldCache = new Dictionary<(Type, string), FieldInfo>();
	private static readonly Dictionary<Type, PropertyInfo> BoolValuePropertyCache = new Dictionary<Type, PropertyInfo>();
	private static FieldInfo GetCachedField(Type type, string name)
	{
		lock (FieldCache)
		{
			if (!FieldCache.TryGetValue((type, name), out var field))
				FieldCache[(type, name)] = field = type.GetField(name, InstanceMemberBindingFlags);
			return field;
		}
	}
	private static PropertyInfo GetCachedBoolValueProperty(Type type)
	{
		if (type == null) return null;
		lock (BoolValuePropertyCache)
		{
			if (!BoolValuePropertyCache.TryGetValue(type, out var property))
				BoolValuePropertyCache[type] = property = type.GetProperty("Value", InstanceMemberBindingFlags);
			return property;
		}
	}
	private static readonly BindingFlags InstanceMemberBindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

	private static readonly FieldInfo PlayerBodyPlayerField = typeof(PlayerBody).GetField("Player", InstanceMemberBindingFlags);

	private static readonly PropertyInfo PlayerBodyPlayerProperty = typeof(PlayerBody).GetProperty("Player", InstanceMemberBindingFlags);

	private static readonly FieldInfo StickPlayerField = typeof(Stick).GetField("Player", InstanceMemberBindingFlags);

	private static readonly PropertyInfo StickPlayerProperty = typeof(Stick).GetProperty("Player", InstanceMemberBindingFlags);

	public class StreamString
	{
		private readonly Stream _ioStream;

		private readonly UnicodeEncoding _streamEncoding;

		public bool IsConnected
		{
			get
			{
				if (_ioStream is NamedPipeClientStream namedPipeClientStream)
				{
					return namedPipeClientStream.IsConnected;
				}
				if (_ioStream is NamedPipeServerStream namedPipeServerStream)
				{
					return namedPipeServerStream.IsConnected;
				}
				return false;
			}
		}

		public StreamString(Stream ioStream)
		{
			_ioStream = ioStream;
			_streamEncoding = new UnicodeEncoding();
		}

		public string ReadString()
		{
			int num = _ioStream.ReadByte() * 256;
			num += _ioStream.ReadByte();
			byte[] array = new byte[num];
			_ioStream.Read(array, 0, num);
			return _streamEncoding.GetString(array);
		}

		public int WriteString(string outString)
		{
			byte[] bytes = _streamEncoding.GetBytes(outString);
			int num = bytes.Length;
			if (num > 65535)
			{
				num = 65535;
			}
			_ioStream.WriteByte((byte)(num / 256));
			_ioStream.WriteByte((byte)(num & 0xFF));
			_ioStream.Write(bytes, 0, num);
			_ioStream.Flush();
			return bytes.Length + 2;
		}

		public void Close()
		{
			_ioStream.Close();
		}
	}

	public static T GetPrivateField<T>(Type typeContainingField, object instanceOfType, string fieldName)
	{
		return (T)GetCachedField(typeContainingField, fieldName).GetValue(instanceOfType);
	}

	public static bool TryGetPrivateField<T>(Type typeContainingField, object instanceOfType, string fieldName, out T value)
	{
		value = default(T);
		if (typeContainingField == null || instanceOfType == null || string.IsNullOrEmpty(fieldName))
		{
			return false;
		}
		FieldInfo field = GetCachedField(typeContainingField, fieldName);
		if (field == null)
		{
			return false;
		}
		object value2 = field.GetValue(instanceOfType);
		if (!(value2 is T))
		{
			return false;
		}
		value = (T)value2;
		return true;
	}

	public static Stick GetStick(GameObject gameObject)
	{
		return ResolveComponentInHierarchy<Stick>(gameObject);
	}

	public static PlayerBody GetPlayerBody(GameObject gameObject)
	{
		return ResolveComponentInHierarchy<PlayerBody>(gameObject);
	}

	public static Stick GetStick(Collision collision)
	{
		return ResolveComponentFromCollision<Stick>(collision);
	}

	public static PlayerBody GetPlayerBody(Collision collision)
	{
		return ResolveComponentFromCollision<PlayerBody>(collision);
	}

	public static Player GetPlayer(PlayerBody playerBody)
	{
		if (playerBody == null)
		{
			return null;
		}
		object value = PlayerBodyPlayerField?.GetValue(playerBody);
		if (value is Player result)
		{
			return result;
		}
		value = PlayerBodyPlayerProperty?.GetValue(playerBody, null);
		if (!(value is Player result2))
		{
			return null;
		}
		return result2;
	}

	public static Player GetPlayer(Stick stick)
	{
		if (stick == null)
		{
			return null;
		}
		object value = StickPlayerField?.GetValue(stick);
		if (value is Player result)
		{
			return result;
		}
		value = StickPlayerProperty?.GetValue(stick, null);
		if (!(value is Player result2))
		{
			return null;
		}
		return result2;
	}

	public static bool GetPlayerBodyBoolField(PlayerBody body, string fieldName)
	{
		if (body == null || string.IsNullOrWhiteSpace(fieldName))
		{
			return false;
		}
		try
		{
			FieldInfo field = GetCachedField(typeof(PlayerBody), fieldName);
			if (field == null)
			{
				return false;
			}
			object value = field.GetValue(body);
			if (value is bool boolValue)
			{
				return boolValue;
			}
			PropertyInfo property = GetCachedBoolValueProperty(value?.GetType());
			if (property != null && property.PropertyType == typeof(bool))
			{
				return (bool)property.GetValue(value, null);
			}
		}
		catch
		{
		}
		return false;
	}

	public static bool GetHasFallen(PlayerBody body)
	{
		return GetPlayerBodyBoolField(body, "HasFallen");
	}

	public static bool GetHasSlipped(PlayerBody body)
	{
		return GetPlayerBodyBoolField(body, "HasSlipped");
	}

	public static string RemoveWhitespace(string input)
	{
		return new string(input.Where((char c) => !char.IsWhiteSpace(c)).ToArray());
	}

	private static T ResolveComponentInHierarchy<T>(GameObject gameObject) where T : Component
	{
		if (gameObject == null)
		{
			return null;
		}
		T component = gameObject.GetComponent<T>();
		if (component != null)
		{
			return component;
		}
		component = gameObject.GetComponentInParent<T>();
		if (component != null)
		{
			return component;
		}
		Rigidbody componentInParent = gameObject.GetComponentInParent<Rigidbody>();
		if (componentInParent == null)
		{
			return null;
		}
		component = componentInParent.GetComponent<T>();
		if (component != null)
		{
			return component;
		}
		return componentInParent.GetComponentInParent<T>();
	}

	private static T ResolveComponentFromCollision<T>(Collision collision) where T : Component
	{
		if (collision == null)
		{
			return null;
		}
		T component = ResolveComponentInHierarchy<T>(collision.gameObject);
		if (component != null)
		{
			return component;
		}
		Collider collider = collision.collider;
		if (collider != null)
		{
			component = collider.GetComponent<T>();
			if (component != null)
			{
				return component;
			}
			component = collider.GetComponentInParent<T>();
			if (component != null)
			{
				return component;
			}
			Rigidbody attachedRigidbody = collider.attachedRigidbody;
			if (attachedRigidbody != null)
			{
				component = attachedRigidbody.GetComponent<T>();
				if (component != null)
				{
					return component;
				}
				component = attachedRigidbody.GetComponentInParent<T>();
				if (component != null)
				{
					return component;
				}
			}
		}
		Rigidbody rigidbody = collision.rigidbody;
		if (rigidbody == null)
		{
			return null;
		}
		component = rigidbody.GetComponent<T>();
		if (component != null)
		{
			return component;
		}
		return rigidbody.GetComponentInParent<T>();
	}
}
