using System.Collections;
using System.Collections.Generic;

namespace Codebase;

public class LockDictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>, IEnumerable
{
	private readonly Dictionary<TKey, TValue> _dictionary = new Dictionary<TKey, TValue>();

	private readonly object _locker = new object();

	public Dictionary<TKey, TValue>.KeyCollection Keys => _dictionary.Keys;

	public Dictionary<TKey, TValue>.ValueCollection Values => _dictionary.Values;

	public int Count => _dictionary.Count;

	public TValue this[TKey index]
	{
		get
		{
			lock (_locker)
			{
				return _dictionary[index];
			}
		}
		set
		{
			lock (_locker)
			{
				_dictionary[index] = value;
			}
		}
	}

	public LockDictionary()
	{
	}

	public LockDictionary(Dictionary<TKey, TValue> dictionary)
	{
		lock (_locker)
		{
			_dictionary = new Dictionary<TKey, TValue>(dictionary);
		}
	}

	public bool TryGetValue(TKey key, out TValue value)
	{
		lock (_locker)
		{
			return _dictionary.TryGetValue(key, out value);
		}
	}

	public void Clear()
	{
		lock (_locker)
		{
			_dictionary.Clear();
		}
	}

	public void Add(TKey key, TValue value)
	{
		lock (_locker)
		{
			_dictionary.Add(key, value);
		}
	}

	public void AddOrUpdate(TKey key, TValue value)
	{
		lock (_locker)
		{
			if (_dictionary.ContainsKey(key))
			{
				_dictionary[key] = value;
			}
			else
			{
				_dictionary.Add(key, value);
			}
		}
	}

	public bool Remove(TKey key)
	{
		lock (_locker)
		{
			return _dictionary.Remove(key);
		}
	}

	public bool ContainsKey(TKey key)
	{
		lock (_locker)
		{
			return _dictionary.ContainsKey(key);
		}
	}

	public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
	{
		lock (_locker)
		{
			return _dictionary.GetEnumerator();
		}
	}

	IEnumerator IEnumerable.GetEnumerator()
	{
		return GetEnumerator();
	}
}
