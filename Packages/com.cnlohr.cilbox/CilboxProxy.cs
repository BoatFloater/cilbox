using UnityEngine;
using System;
using System.Collections.Generic;
using System.Reflection;

#if UNITY_EDITOR
using Unity.Profiling;
#endif

namespace Cilbox
{
	public class CilboxProxy : MonoBehaviour, ISerializationCallbackReceiver
	{
		public StackElement [] fields;
		public List< UnityEngine.Object > fieldsObjects;  // This is generally only held during saving and loading, not in use.
		[NonSerialized] private List< UnityEngine.Object > runtimeFieldsObjects;

		public CilboxClass cls;
		public Cilbox box;
		public String className;
		public String serializedObjectData;

		public String buildTimeGuid;
		public String initialLoadPath;

		private bool proxyWasSetup = false;
		private bool proxyLoadInProgress = false;

		public bool disabled = false;

		// When a loaded proxy is cloned through Object.Instantiate(), Unity serializes and
		// deserializes it.  These fields carry the proxy's *current* CIL field values through
		// that serialization pass.  The object table is a serialized List<UnityEngine.Object>
		// so Unity's internal-reference remapping applies to it for free; the data string only
		// contains indices into that table.  They are deliberately separate from the build-time
		// template (serializedObjectData/fieldsObjects), which remains untouched.
		[SerializeField, HideInInspector] private string runtimeSnapshotData;
		[SerializeField, HideInInspector] private List< UnityEngine.Object > runtimeSnapshotObjects;
		[SerializeField, HideInInspector] private string runtimeSnapshotSession;

		// Schema (one entry per CIL instance field, null when the field was not serialized by
		// SetupProxy) used to reproduce the build-time encoding decisions for snapshots.
		[NonSerialized] private SerializedProxyField [] serializedFieldSchemas;

		// Runtime snapshots are only valid for the process that created them.  This keeps a
		// scene/prefab saved during an editor play session from being treated as a template
		// when it is loaded again in a later session or in a build.
		private static readonly string runtimeSessionId = Guid.NewGuid().ToString( "N" );

		public void DisableProxy()
		{
			if( disabled ) return;
			disabled = true;
			enabled = false;
		}

		private void ProxyDebugLog( string message )
		{
				Debug.Log( $"[CilboxProxy:{gameObject.name}] {message}" );
		}

		public CilboxProxy() { }

#if UNITY_EDITOR
		public void SetupProxy( Cilbox box, MonoBehaviour mToSteal, Dictionary< MonoBehaviour, CilboxProxy > refToProxyMap )
		{
			this.box = box;
			this.className = mToSteal.GetType().ToString();

			box.BoxInitialize();
			cls = box.GetClass( className );

			fieldsObjects = new List< UnityEngine.Object >();
			FieldInfo[] fi = CilboxUtil.GetInstanceFieldsBaseFirst( mToSteal.GetType() );

			List< SerializedProxyField > lstFields = new List< SerializedProxyField >();

			foreach( var f in fi )
			{
				// TODO: Consider if we should try serializing _everything_.  No because arrays really need their constructors.
				if( !f.IsPublic && f.GetCustomAttributes(typeof(SerializeField), true).Length <= 0 )
					continue;

				object fv = f.GetValue( mToSteal );

				int matchingInstanceNameID = -1;
				for( int k = 0; k < cls.instanceFieldNames.Length; k++ )
				{
					if( cls.instanceFieldNames[k] == f.Name )
					{
						matchingInstanceNameID = k;
					}
				}


				if( matchingInstanceNameID < 0 )
				{
					Debug.Log( $"Warning: Could not find matching instance name for {f.Name}" );
					continue;
				}

				SerializedProxyField spf = SerializeProxyField(fv, f.Name, matchingInstanceNameID, fieldsObjects, refToProxyMap);

				// Serialize no matter what.
				lstFields.Add( spf );
			}

			SerializedProxy proxy = new SerializedProxy
			{
				fields = lstFields.ToArray(),
			};
			serializedObjectData = proxy.SerializeString();

			buildTimeGuid = Guid.NewGuid().ToString();
		}


		private static SerializedProxyField SerializeProxyField( object fv, String fName, int matchingInstanceNameID, List< UnityEngine.Object > objectSlots, Dictionary< MonoBehaviour, CilboxProxy > refToProxyMap = null, SerializedProxyField schema = null, HashSet< CilboxHeapInstance > heapStack = null )
		{
			SerializedProxyField spf = new SerializedProxyField();
			spf.fieldName = fName;
			spf.matchingInstanceId = matchingInstanceNameID;

			// Skip null objects.
			if( fv == null )
			{
				spf.fieldType = (byte)ProxyFieldType.Empty;
				return spf;
			}

			Type fvType = fv.GetType();
			bool hasCilboxable = CilboxUtil.HasCilboxableAttribute( fvType );

			StackType st;

			// Serialize enum field as underlying type
			if( fvType.IsEnum )
			{
				object underlying = Convert.ChangeType( fv, fvType.GetEnumUnderlyingType() );
				if( StackElement.TypeToStackType.TryGetValue( underlying.GetType().ToString(), out st ) && st < StackType.Object )
				{
					spf.fieldType = (byte)ProxyFieldType.Primitive;
					spf.primitiveValue.Unbox( underlying, st );
				}
			}
			// Runtime-created cilbox objects (newobj) are plain managed objects.  Deep-copy
			// their fields recursively.  Cycles are broken (they cannot be represented in this
			// inline format) and aliasing is not preserved; both are runtime-only constructs
			// with no direct Unity Instantiate analogue.
			else if( fv is CilboxHeapInstance heap )
			{
				if( heapStack == null )
					heapStack = new HashSet< CilboxHeapInstance >();
				if( !heapStack.Add( heap ) )
				{
					spf.fieldType = (byte)ProxyFieldType.Unsupported;
					return spf;
				}

				try
				{
					spf.fieldType = (byte)ProxyFieldType.CilboxHeap;
					spf.data = heap.className;
					int heapFieldCount = heap.fields != null ? heap.fields.Length : 0;
					spf.arrayElements = new SerializedProxyField[heapFieldCount];
					for( int i = 0; i < heapFieldCount; i++ )
					{
						StackElement he = heap.fields[i];
						String hname = heap.cls != null && heap.cls.instanceFieldNames != null && i < heap.cls.instanceFieldNames.Length ? heap.cls.instanceFieldNames[i] : null;
						if( he.type == StackType.Address || he.type == StackType.NativeHandle )
						{
							spf.arrayElements[i] = new SerializedProxyField { fieldType = (byte)ProxyFieldType.Unsupported, matchingInstanceId = i, fieldName = hname };
							continue;
						}
						object hv = he.AsObject();
						spf.arrayElements[i] = SerializeProxyField( hv, hname, i, objectSlots, null, BuildHeapFieldSchema( heap.cls, i ), heapStack );
					}
				}
				finally
				{
					heapStack.Remove( heap );
				}
			}
			// A runtime field value for a cilboxed class is already a CilboxProxy.  At build time
			// the value is the original [Cilboxable] MonoBehaviour instead, which is handled below.
			else if( fv is CilboxProxy cilboxProxy )
			{
				spf.fieldType = (byte)ProxyFieldType.CilboxRef;
				spf.fieldObjectIndex = objectSlots.Count;
				spf.objectRefName = fv.ToString();
				spf.objectRefIsNull = spf.objectRefName == "null";
				objectSlots.Add( cilboxProxy );
			}
			// Not a proxiable script.
			else if (hasCilboxable)
			{
				spf.fieldType = (byte)ProxyFieldType.CilboxRef;
				spf.fieldObjectIndex = objectSlots.Count;
				spf.objectRefName = fv.ToString();
				spf.objectRefIsNull = spf.objectRefName == "null";

				UnityEngine.Object mapped = null;
				if( refToProxyMap != null && fv is MonoBehaviour mb && refToProxyMap.TryGetValue( mb, out CilboxProxy proxy ) )
					mapped = proxy;
				else if( fv is UnityEngine.Object uo )
					mapped = uo;

				objectSlots.Add( mapped );
			}
			else if( fv is UnityEngine.Object )
			{
				spf.fieldType = (byte)ProxyFieldType.ObjectRef;
				spf.fieldObjectIndex = objectSlots.Count;
				spf.objectRefName = fv.ToString();
				spf.objectRefIsNull = spf.objectRefName == "null";
				objectSlots.Add( (UnityEngine.Object)fv );
			}
			else if( fv is string )
			{
				spf.fieldType = (byte)ProxyFieldType.String;
				spf.data = fv.ToString();
			}
			else if( StackElement.TypeToStackType.TryGetValue( fvType.ToString(), out st ) && st < StackType.Object )
			{
				spf.fieldType = (byte)ProxyFieldType.Primitive;
				spf.primitiveValue.Unbox( fv, st );
			}
			else if( fvType.IsArray )
			{
				spf.fieldType = (byte)ProxyFieldType.Array;
				Type type = fvType.GetElementType();
				spf.elementType = schema != null && schema.fieldType == (byte)ProxyFieldType.Array && schema.elementType != null
					? schema.elementType
					: SerializedTypeDescriptorBuilder.FromNativeType( type );
				Array arr = (Array)fv;
				int len = arr.Length;
				if( type == typeof( object ) || typeof( CilboxHeapInstance ).IsAssignableFrom( type ) )
					spf.data = "o"; // preserve object[] on load for runtime-created cilbox arrays
				SerializedProxyField[] schemaElements = schema != null ? schema.arrayElements : null;
				spf.arrayElements = new SerializedProxyField[len];
				for( int i = 0; i < len; i++ )
				{
					SerializedProxyField childSchema = schemaElements != null && i < schemaElements.Length ? schemaElements[i] : null;
					object o = arr.GetValue(i);
					spf.arrayElements[i] = SerializeProxyField( o, null, -1, objectSlots, refToProxyMap, childSchema, heapStack );
				}
			}
			else
			{
				spf.fieldType = (byte)ProxyFieldType.Json;
				spf.data = JsonUtility.ToJson(fv);
				spf.elementType = schema != null && schema.fieldType != (byte)ProxyFieldType.Array && schema.elementType != null
					? schema.elementType
					: SerializedTypeDescriptorBuilder.FromNativeType( fvType );
			}

			return spf;
		}

		private static SerializedProxyField BuildHeapFieldSchema( CilboxClass hcls, int index )
		{
			if( hcls == null || hcls.instanceFieldTypeDescriptors == null ||
				index < 0 || index >= hcls.instanceFieldTypeDescriptors.Length )
				return null;

			SerializedTypeDescriptor td = hcls.instanceFieldTypeDescriptors[index];
			if( td == null )
				return null;

			SerializedProxyField schema = new SerializedProxyField();
			schema.matchingInstanceId = index;
			if( td.typeName != null && td.typeName.EndsWith( "[]" ) )
			{
				schema.fieldType = (byte)ProxyFieldType.Array;
				schema.elementType = new SerializedTypeDescriptor
				{
					assemblyName = td.assemblyName,
					typeName = td.typeName.Substring( 0, td.typeName.Length - 2 ),
					genericName = td.genericName,
					genericArgs = td.genericArgs,
					underlyingType = td.underlyingType,
				};
			}
			else
			{
				schema.elementType = td;
			}
			return schema;
		}

#endif

		public void OnBeforeSerialize()
		{
			// Unloaded proxies keep their build-time payload untouched.
			if( !proxyWasSetup || fields == null || proxyLoadInProgress )
				return;

			try
			{
				CaptureRuntimeSnapshot();
			}
			catch( Exception e )
			{
				// Never let a snapshot failure break Instantiate.  A clone will fall back to
				// the build-time payload, if one is still available.
				runtimeSnapshotData = null;
				runtimeSnapshotObjects = null;
				runtimeSnapshotSession = null;
				Debug.LogWarning( $"[CilboxProxy:{className}] Failed to capture runtime snapshot: {e.Message}" );
			}
		}

		public void OnAfterDeserialize()
		{
			// Intentionally empty.  RuntimeProxyLoad runs from Awake/Start, on the main thread,
			// after Unity has assigned (and remapped) all serialized fields.  Doing load work here
			// would be unsafe for scene deserialization and is skipped entirely by InstantiateAsync
			// in some Unity versions.
		}

		// Serializes the live CIL field state into the same payload format SetupProxy uses, but
		// only for fields that SetupProxy would have serialized.  Non-serialized/private fields
		// must keep Unity's Instantiate semantics: the clone gets the constructor/default value.
		private void CaptureRuntimeSnapshot()
		{
			if( serializedFieldSchemas == null )
				return;

			List< SerializedProxyField > lstFields = new List< SerializedProxyField >();
			List< UnityEngine.Object > objectSlots = new List< UnityEngine.Object >();

			for( int i = 0; i < serializedFieldSchemas.Length; i++ )
			{
				SerializedProxyField schema = serializedFieldSchemas[i];
				if( schema == null )
					continue;

				object value;
				String fieldName = cls != null && i < cls.instanceFieldNames.Length ? cls.instanceFieldNames[i] : null;
				if( fields[i].type == StackType.Address || fields[i].type == StackType.NativeHandle )
				{
					// Runtime-only values cannot be transferred to another instance.  Leave the
					// clone's default/constructor value in place rather than clobbering it.
					lstFields.Add( new SerializedProxyField { fieldType = (byte)ProxyFieldType.Unsupported, matchingInstanceId = i, fieldName = fieldName } );
					continue;
				}
				else
				{
					value = fields[i].AsObject( box );
				}

				lstFields.Add( SerializeProxyField( value, fieldName, i, objectSlots, null, schema ) );
			}

			SerializedProxy proxy = new SerializedProxy
			{
				fields = lstFields.ToArray(),
			};

			string newData = proxy.SerializeString();
			bool same = newData == runtimeSnapshotData &&
				runtimeSnapshotObjects != null &&
				runtimeSnapshotObjects.Count == objectSlots.Count;
			if( same )
			{
				for( int i = 0; i < objectSlots.Count; i++ )
				{
					if( !ReferenceEquals( runtimeSnapshotObjects[i], objectSlots[i] ) )
					{
						same = false;
						break;
					}
				}
			}

			if( !same )
			{
				runtimeSnapshotData = newData;
				runtimeSnapshotObjects = objectSlots;
			}
			runtimeSnapshotSession = runtimeSessionId;
		}

		void Awake()
		{
			// You cannot do anything in Awake()  Box is not set yet.
		}

		public void RuntimeProxyLoad()
		{
			//Debug.Log( "Runtime Proxy Load " + proxyWasSetup + " " + transform.name + " " + className );
			if( proxyWasSetup ) return;
			if( proxyLoadInProgress ) return;
			if (box == null) return;

			// Prefer a snapshot captured by OnBeforeSerialize() for this object's clone.  A
			// snapshot from another session (for example, a scene saved while in editor Play
			// mode) is stale and must never be treated as a template.
			bool fromSnapshot = !string.IsNullOrEmpty(runtimeSnapshotData) && runtimeSnapshotSession == runtimeSessionId;
			if( !string.IsNullOrEmpty(runtimeSnapshotData) && !fromSnapshot )
			{
				runtimeSnapshotData = null;
				runtimeSnapshotObjects = null;
				runtimeSnapshotSession = null;
			}

			string sourceData = fromSnapshot ? runtimeSnapshotData : serializedObjectData;
			List< UnityEngine.Object > sourceObjects = fromSnapshot ? runtimeSnapshotObjects : fieldsObjects;

			if (string.IsNullOrEmpty(sourceData)) return;
			proxyLoadInProgress = true;
			try
			{
				box.BoxInitialize(); // In case it is not yet initialized.
				bool verboseLogging = box.verboseLogging;

#if UNITY_EDITOR
				using var initMarker = new ProfilerMarker($"Initialize {className}").Auto();
#endif
				var sb = new System.Text.StringBuilder("/" + transform.name);
				Transform aparent = transform.parent;
				while (aparent != null)
				{
					sb.Insert(0, aparent.name).Insert(0, '/');
					aparent = aparent.parent;
				}
				initialLoadPath = sb.ToString();

				if (string.IsNullOrEmpty(className))
				{
					Debug.LogError( $"[CilboxProxy:{gameObject.name}] RuntimeProxyLoad aborted: class {className} was not found in Cilbox assembly data." );
					return;
				}

				cls = box.GetClass( className );

				runtimeFieldsObjects = sourceObjects != null ? new List<UnityEngine.Object>(sourceObjects) : new List<UnityEngine.Object>();

				// First thing: Go through any references that are prohibited.
				for( int i = 0; i < runtimeFieldsObjects.Count; i++ )
				{
					UnityEngine.Object o = runtimeFieldsObjects[i];
					if (o == null)
					{
						// If it's null, there's nothing to safety-check.
						continue;
					}
					Type t = o.GetType();
					if(box.GetTypeOverride( t.FullName, out Type overrideType )) {
						Debug.Log( $"RuntimeProxyLoad: Override {t.FullName} with {overrideType.FullName}" );
						t = overrideType;
						if(typeof(CilboxShim).IsAssignableFrom(t) && runtimeFieldsObjects[i] is Component gameObjectComponent)
						{
							GameObject gameObject = gameObjectComponent.gameObject;
							Component component;
							if(gameObject.TryGetComponent(t, out Component c)) {
								component = c;
							} else
							{
								component = gameObject.AddComponent(t);
							}
							runtimeFieldsObjects[i] = component;
						}
					}
					if( t == typeof( CilboxProxy ) )
					{
						// If it's another cilbox proxy, it's OK.
					}
					else if( !box.CheckTypeAllowed( t ) )
					{
						Debug.LogWarning( $"Contraband found in script {className} field ID {i}: {o.GetType()}" );
						runtimeFieldsObjects[i] = null;
					}
				}

				// Populate fields[]
				int fieldCount = cls.instanceFieldNames.Length;
				fields = new StackElement[fieldCount];

				SerializedProxy proxyData = SerializedProxy.DeserializeString( sourceData );

				serializedFieldSchemas = new SerializedProxyField[fieldCount];
				foreach( SerializedProxyField spf in proxyData.fields )
				{
					if( spf.matchingInstanceId >= 0 && spf.matchingInstanceId < fieldCount )
					{
						// Keep Empty entries too: they mark a field that the build serializer
						// considered serialized (it was null at build time), so later snapshots
						// must include it.
						serializedFieldSchemas[spf.matchingInstanceId] = spf;
					}
				}

				// Preinitialize every field to its CLR default value so that non-serialized fields
				// (especially UnityEngine.Object references) are not left as implicit StackType.Boolean.
				for( int i = 0; i < fieldCount; i++ )
				{
					Type fieldType = cls.instanceFieldTypes[i];
					// Maybe need to GetComponentTypeOverride here as well?  Maybe not, since that should only be for actual UnityEngine.Objects, which should be null at this point if they are contraband.
					if( fieldType == null )
					{
						fields[i].LoadObject( null );
						continue;
					}
					StackType st = StackElement.StackTypeFromType( fieldType );
					if( st < StackType.Object )
					{
						fields[i].type = st;
						if (verboseLogging)
							ProxyDebugLog( $"Default field init {cls.instanceFieldNames[i]} <- default({fieldType})" );
					}
					else if( fieldType.IsValueType )
					{
						try
						{
							// We clean the fieldtype before https://github.com/cnlohr/cilbox/blob/fc608341d293186e0aacf519ea9f0beb43d42cee/Packages/com.cnlohr.cilbox/Cilbox.cs#L1389C40-L1389C67
							object defaultValue = Activator.CreateInstance( fieldType );
							fields[i].LoadObject( defaultValue );
							if (verboseLogging)
								ProxyDebugLog( $"Default field init {cls.instanceFieldNames[i]} <- default({fieldType}) [boxed]" );
						}
						catch( Exception e )
						{
							fields[i].LoadObject( null );
							Debug.LogWarning( $"[CilboxProxy:{gameObject.name}] Failed to create default value for {cls.instanceFieldNames[i]} ({fieldType}): {e.Message}" );
						}
					}
					else if( fieldType == typeof(string) )
					{
						// Empty/unset string fields default to string.Empty (matches Unity), not null.
						fields[i].LoadObject( string.Empty );
						if (verboseLogging)
							ProxyDebugLog( $"Default field init {cls.instanceFieldNames[i]} <- string.Empty" );
					}
					else
					{
						fields[i].LoadObject( null );
						if (verboseLogging)
							ProxyDebugLog( $"Default field init {cls.instanceFieldNames[i]} <- null" );
					}
				}

				// Call interpreted constructor.
				cls.InterpretMethod(this, ImportFunctionID.dotCtor, null);

				// load serialized fields.
				for( int i = 0; i < fieldCount; i++ )
				{
					SerializedProxyField spf = serializedFieldSchemas[i];

					if( spf == null ) { /* Debug.Log( $"Skipping {i} {cls.instanceFieldNames[i]}" ); */ continue; }

					ProxyFieldType ft = (ProxyFieldType)spf.fieldType;
					if( ft == ProxyFieldType.Empty )
					{
						// Explicitly null in the payload.  Unity copies null over the clone's
						// constructor/default value; do the same (notably for strings).
						fields[i].LoadObject( null );
						continue;
					}

					if( ft == ProxyFieldType.Primitive )
					{
						spf.primitiveValue.ToStackElement(ref fields[i]);
						continue;
					}

					LoadProxyFieldStackElement( spf, ref fields[i], cls.instanceFieldNames[i] );
				}


				proxyWasSetup = true;
				runtimeFieldsObjects = null;
				serializedObjectData = null;
				if( fromSnapshot )
				{
					// This instance has already consumed its clone payload.  A future clone will
					// capture a fresh snapshot from its own live fields.
					runtimeSnapshotData = null;
					runtimeSnapshotObjects = null;
					runtimeSnapshotSession = null;
				}
				if (verboseLogging)
					Debug.Log( $"RuntimeProxyLoad complete for class {className}" );
			}
			finally
			{
				proxyLoadInProgress = false;
			}
		}


		// Loads the reference StackElement with the appropriate data
		private void LoadProxyFieldStackElement( SerializedProxyField spf, ref StackElement refElement, String rootFieldName )
		{
			ProxyFieldType ft = (ProxyFieldType)spf.fieldType;
			List<UnityEngine.Object> objectSlots = runtimeFieldsObjects ?? fieldsObjects;

			switch (ft)
			{
			case ProxyFieldType.CilboxRef:
			case ProxyFieldType.ObjectRef:
			{
				int iFO = spf.fieldObjectIndex;
				if (iFO < 0 || iFO >= objectSlots.Count) // break early if index is out of bounds
				{
					Debug.LogWarning(
						$"Failure to load object in field id:{rootFieldName} of {className} (slot out of range, fieldsObjects count={objectSlots.Count})");
					break;
				}

				if (spf.objectRefIsNull)
				{
					// This field was null when serialized, so just load null
					refElement.LoadObject(null);
					return;
				}

				UnityEngine.Object o = objectSlots[iFO];

				if (o)
				{
					if (o is CilboxProxy cilboxProxy)
						cilboxProxy.RuntimeProxyLoad();

					refElement.LoadObject(o);

					// Remove reference out of the fieldsObjects array.
					objectSlots[iFO] = null;

					return;
				}

				Debug.LogWarning(
					$"[CilboxProxy:{gameObject.name}] Object reference slot {iFO} for field {rootFieldName} is null/missing at load time.");

				break;
			}

			case ProxyFieldType.CilboxHeap:
			{
				CilboxClass hcls = box.GetClass( spf.data );
				if( hcls == null )
				{
					Debug.LogWarning( $"[CilboxProxy:{gameObject.name}] Could not load cilbox heap class {spf.data} for field {rootFieldName}." );
					refElement.LoadObject( null );
					return;
				}

				CilboxHeapInstance heap = new CilboxHeapInstance
				{
					className = spf.data,
					cls = hcls,
					fields = new StackElement[ hcls.instanceFieldNames.Length ],
				};
				InitHeapFields( heap, hcls );

				if( spf.arrayElements != null )
				{
					foreach( SerializedProxyField child in spf.arrayElements )
					{
						if( child == null ) continue;
						int idx = child.matchingInstanceId;
						if( idx < 0 || idx >= heap.fields.Length ) continue;
						if( (ProxyFieldType)child.fieldType == ProxyFieldType.Unsupported ) continue;
						if( (ProxyFieldType)child.fieldType == ProxyFieldType.Primitive )
						{
							child.primitiveValue.ToStackElement( ref heap.fields[idx] );
							continue;
						}
						LoadProxyFieldStackElement( child, ref heap.fields[idx], rootFieldName + "." + hcls.instanceFieldNames[idx] );
					}
				}

				refElement.LoadObject( heap );
				return;
			}

			case ProxyFieldType.Array:
			{
				Type t = box.usage.GetNativeTypeFromDescriptor( spf.elementType );
				bool isCilboxElementType = false;
				bool forceObjectArray = spf.data == "o";

				if (t == null)
				{
					if (box.classes.ContainsKey(spf.elementType.typeName))
					{
						// Check the array to see if it is Cilboxed
						t = typeof(CilboxProxy);
						isCilboxElementType = true;
					}
					else // type is null and not a Cilbox class
					{
						refElement.LoadObject(null);
						return;
					}
				}

				if( forceObjectArray )
				{
					// Arrays created by newarr for cilbox classes are object[], and runtime-created
					// CilboxHeapInstance elements cannot go into CilboxProxy[].
					t = typeof(object);
					isCilboxElementType = true; // skip the type whitelist check, as for CilboxProxy arrays
				}
				else if( isCilboxElementType && spf.arrayElements != null )
				{
					foreach( SerializedProxyField child in spf.arrayElements )
					{
						if( child != null && (ProxyFieldType)child.fieldType == ProxyFieldType.CilboxHeap )
						{
							t = typeof(object);
							break;
						}
					}
				}

				if( !isCilboxElementType && !box.CheckTypeAllowed( t ) )
				{
					proxyWasSetup = false;
					throw new Exception( $"Contraband ARRAY found in script {className} field {rootFieldName}" );
				}

				int aLen = spf.arrayElements.Length;
				Array arr = Array.CreateInstance( t, aLen );

				StackType elementSt = default;
				bool isPrimArr = !isCilboxElementType
					&& StackElement.TypeToStackType.TryGetValue(t.ToString(), out elementSt)
					&& elementSt < StackType.Object;

				if( isPrimArr )
				{
					// Typed array fill — one cast outside the loop, direct typed writes inside. Zero boxes.
					switch( elementSt )
					{
					case StackType.Boolean: { var a = (bool[])  arr; for (int j = 0; j < aLen; j++) a[j] =         spf.arrayElements[j].primitiveValue.b; break; }
					case StackType.Sbyte:   { var a = (sbyte[]) arr; for (int j = 0; j < aLen; j++) a[j] = (sbyte) spf.arrayElements[j].primitiveValue.l; break; }
					case StackType.Byte:    { var a = (byte[])  arr; for (int j = 0; j < aLen; j++) a[j] = (byte)  spf.arrayElements[j].primitiveValue.e; break; }
					case StackType.Short:   { var a = (short[]) arr; for (int j = 0; j < aLen; j++) a[j] = (short) spf.arrayElements[j].primitiveValue.l; break; }
					case StackType.Ushort:  { var a = (ushort[])arr; for (int j = 0; j < aLen; j++) a[j] = (ushort)spf.arrayElements[j].primitiveValue.e; break; }
					case StackType.Int:     { var a = (int[])   arr; for (int j = 0; j < aLen; j++) a[j] = (int)   spf.arrayElements[j].primitiveValue.l; break; }
					case StackType.Uint:    { var a = (uint[])  arr; for (int j = 0; j < aLen; j++) a[j] = (uint)  spf.arrayElements[j].primitiveValue.e; break; }
					case StackType.Long:    { var a = (long[])  arr; for (int j = 0; j < aLen; j++) a[j] =         spf.arrayElements[j].primitiveValue.l; break; }
					case StackType.Ulong:   { var a = (ulong[]) arr; for (int j = 0; j < aLen; j++) a[j] =         spf.arrayElements[j].primitiveValue.e; break; }
					case StackType.Float:   { var a = (float[]) arr; for (int j = 0; j < aLen; j++) a[j] =         spf.arrayElements[j].primitiveValue.f; break; }
					case StackType.Double:  { var a = (double[])arr; for (int j = 0; j < aLen; j++) a[j] =         spf.arrayElements[j].primitiveValue.d; break; }
					}
				}
				else // non-primitive array
				{
					for( int j = 0; j < aLen; j++ )
					{
						SerializedProxyField child = spf.arrayElements[j];
						if( (ProxyFieldType)child.fieldType == ProxyFieldType.Unsupported )
						{
							arr.SetValue( t.IsValueType ? Activator.CreateInstance( t ) : null, j );
							continue;
						}
						StackElement temp = default;
						LoadProxyFieldStackElement( child, ref temp, rootFieldName );
						arr.SetValue( temp.AsObject(), j );
					}
				}

				refElement.LoadObject(arr);
				return;
			}

			case ProxyFieldType.String:
			{
				refElement.LoadObject(spf.data);
				return;
			}

			case ProxyFieldType.Primitive:
			{
				spf.primitiveValue.ToStackElement(ref refElement);
				return;
			}

			case ProxyFieldType.Json:
			{
				Type t = box.usage.GetNativeTypeFromDescriptor( spf.elementType );
				refElement.LoadObject(JsonUtility.FromJson(spf.data, t));
				return;
			}

			case ProxyFieldType.Unsupported:
				// Runtime-only value; the preinitialized default/constructor value is correct.
				return;

			case ProxyFieldType.Empty:
			default:
				break;
			}

			refElement.LoadObject(null);
		}

		private static void InitHeapFields( CilboxHeapInstance heap, CilboxClass hcls )
		{
			for( int i = 0; i < heap.fields.Length; i++ )
			{
				Type fieldType = hcls.instanceFieldTypes[i];
				if( fieldType == null )
				{
					heap.fields[i].LoadObject( null );
					continue;
				}

				StackType st = StackElement.StackTypeFromType( fieldType );
				if( st < StackType.Object )
				{
					heap.fields[i].type = st;
				}
				else if( fieldType.IsValueType )
				{
					try
					{
						heap.fields[i].LoadObject( Activator.CreateInstance( fieldType ) );
					}
					catch
					{
						heap.fields[i].LoadObject( null );
					}
				}
				else if( fieldType == typeof( string ) )
				{
					heap.fields[i].LoadObject( string.Empty );
				}
				else
				{
					heap.fields[i].LoadObject( null );
				}
			}
		}


		void Start() {
			RuntimeProxyLoad();

			if( proxyWasSetup ) {
				// Call Awake after initialization.
				cls.InterpretMethod(this, ImportFunctionID.Awake, null);
				cls.InterpretMethod(this, ImportFunctionID.Start, null);
			}
		}
		void FixedUpdate() { if( proxyWasSetup ) cls.InterpretMethod(this, ImportFunctionID.FixedUpdate, null); }
		void Update() { if( proxyWasSetup ) cls.InterpretMethod(this, ImportFunctionID.Update, null); }
		void LateUpdate() { if( proxyWasSetup ) cls.InterpretMethod(this, ImportFunctionID.LateUpdate, null); }
		void OnEnable() { if( proxyWasSetup ) cls.InterpretMethod(this, ImportFunctionID.OnEnable, null); }
		void OnDisable() { if( proxyWasSetup ) cls.InterpretMethod(this, ImportFunctionID.OnDisable, null); }
		void OnDestroy() { if( proxyWasSetup ) cls.InterpretMethod(this, ImportFunctionID.OnDestroy, null); }
		void OnTriggerEnter(Collider c) { if (proxyWasSetup) cls.InterpretMethod(this, ImportFunctionID.OnTriggerEnter, new object[] { c }); }
		void OnTriggerExit(Collider c) { if (proxyWasSetup) cls.InterpretMethod(this, ImportFunctionID.OnTriggerExit, new object[] { c }); }
		void OnCollisionEnter(Collision c) { if (proxyWasSetup) cls.InterpretMethod(this, ImportFunctionID.OnCollisionEnter, new object[] { c }); }
		void OnCollisionExit(Collision c) { if (proxyWasSetup) cls.InterpretMethod(this, ImportFunctionID.OnCollisionExit, new object[] { c }); }
		void OnTriggerStay(Collider c) { if (proxyWasSetup) cls.InterpretMethod(this, ImportFunctionID.OnTriggerStay, new object[] { c }); }
		void OnCollisionStay(Collision c) { if (proxyWasSetup) cls.InterpretMethod(this, ImportFunctionID.OnCollisionStay, new object[] { c }); }
		void OnRenderObject() { if (proxyWasSetup) cls.InterpretMethod(this, ImportFunctionID.OnRenderObject, null); }
		void OnWillRenderObject() { if (proxyWasSetup) cls.InterpretMethod(this, ImportFunctionID.OnWillRenderObject, null); }
	}
}

