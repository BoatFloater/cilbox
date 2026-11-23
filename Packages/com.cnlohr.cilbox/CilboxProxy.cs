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
		private int heapLoadDepth = 0;

		public bool disabled = false;

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

				// Only what Unity itself would serialize.
				if( !cls.IsInstanceFieldSerialized( matchingInstanceNameID ) )
					continue;

				object fv = f.GetValue( mToSteal );
				SerializedProxyField spf = SerializeProxyField(fv, f.Name, matchingInstanceNameID, cls.instanceFieldRows[matchingInstanceNameID].type, refToProxyMap);

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

		// At build time an instance of a plain Cilbox class is still the native object: serialize it like a CilboxHeapInstance.
		private void SerializeNativePlainObject( SerializedProxyField spf, object fv, Dictionary< MonoBehaviour, CilboxProxy > refToProxyMap, int depth )
		{
			Type type = fv.GetType();
			CilboxClass plainClass = box.GetClass( type.FullName );
			if( plainClass == null || depth > MaxSerializeDepth )
			{
				spf.fieldType = (byte)ProxyFieldType.Empty;
				return;
			}

			spf.fieldType = (byte)ProxyFieldType.CilboxHeap;
			spf.data = type.FullName;
			List< SerializedProxyField > lstFields = new List< SerializedProxyField >();
			foreach( FieldInfo f in CilboxUtil.GetInstanceFieldsBaseFirst( type ) )
			{
				int index = Array.IndexOf( plainClass.instanceFieldNames, f.Name );
				if( index < 0 || !plainClass.IsInstanceFieldSerialized( index ) )
					continue;
				lstFields.Add( SerializeProxyField( f.GetValue( fv ), f.Name, index, plainClass.instanceFieldRows[index].type, refToProxyMap, depth + 1, plainClass.className ) );
			}
			spf.arrayElements = lstFields.ToArray();
		}

#endif

		// Nesting of plain classes that gets serialized, matching Unity: a chain of nested instances is cut off after
		// 11 (the field's own instance plus 10 levels).
		private const int MaxSerializeDepth = 10;

		// Serializes the Unity-serialized instance fields of a class instance.
		private SerializedProxyField[] SerializeInstanceFields( CilboxClass instanceClass, StackElement[] values, int depth )
		{
			List< SerializedProxyField > lstFields = new List< SerializedProxyField >();
			for( int i = 0; i < values.Length; i++ )
			{
				if( !instanceClass.IsInstanceFieldSerialized( i ) )
					continue;
				lstFields.Add( SerializeProxyField( values[i].AsObject( box ), instanceClass.instanceFieldNames[i], i, instanceClass.instanceFieldRows[i].type, null, depth, instanceClass.className ) );
			}
			return lstFields.ToArray();
		}

		// An array type's descriptor names "T[]" and does not describe T.  Cilbox classes and enums have their own
		// descriptor (with flags, and the underlying type for enums); other element types are only needed by name.
		private SerializedTypeDescriptor GetDeclaredElementType( SerializedTypeDescriptor arrayType )
		{
			string elementName = arrayType.typeName.Substring( 0, arrayType.typeName.Length - 2 );
			if( box.GetClass( elementName ) is CilboxClass elementClass && elementClass.type != null )
				return elementClass.type;
			if( box.cilboxEnums != null && box.cilboxEnums.TryGetValue( elementName, out CilboxEnum elementEnum ) && elementEnum.type != null )
				return elementEnum.type;
			return new SerializedTypeDescriptor { assemblyName = arrayType.assemblyName, typeName = elementName };
		}

		// declaredType is the declared type of the field (or of the array's element, when recursing).  Like Unity's
		// serializer, null strings become "", null arrays become empty, and null plain classes become a fresh
		// instance (except in a field of the class's own type: ownerClassName is the class that declares the field).
		private SerializedProxyField SerializeProxyField( object fv, String fName, int matchingInstanceNameID, SerializedTypeDescriptor declaredType, Dictionary< MonoBehaviour, CilboxProxy > refToProxyMap, int depth = 0, string ownerClassName = null )
		{
			SerializedProxyField spf = new SerializedProxyField();
			spf.fieldName = fName;
			spf.matchingInstanceId = matchingInstanceNameID;

			String declaredName = declaredType?.typeName;
			bool declaredArray = declaredName != null && declaredName.EndsWith( "[]" );
			SerializedTypeDescriptor declaredElementType = null;
			if( declaredArray )
				declaredElementType = GetDeclaredElementType( declaredType );

			if( fv == null )
			{
				if( declaredName == "System.String" )
				{
					spf.fieldType = (byte)ProxyFieldType.String;
					spf.data = "";
				}
				else if( declaredArray )
				{
					spf.fieldType = (byte)ProxyFieldType.Array;
					spf.elementType = declaredElementType;
					spf.arrayElements = new SerializedProxyField[0];
				}
				else if( declaredName != null && declaredName != ownerClassName && box.GetClass( declaredName )?.IsPlainClass() == true )
				{
					spf.fieldType = (byte)ProxyFieldType.CilboxHeap;
					spf.data = declaredName;
					spf.arrayElements = new SerializedProxyField[0];
				}
				else
				{
					spf.fieldType = (byte)ProxyFieldType.Empty;
				}
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
			// An instance of a plain Cilbox class created at runtime.
			else if( fv is CilboxHeapInstance heap )
			{
				if( depth > MaxSerializeDepth )
				{
					spf.fieldType = (byte)ProxyFieldType.Empty;
					return spf;
				}
				spf.fieldType = (byte)ProxyFieldType.CilboxHeap;
				spf.data = heap.className;
				spf.arrayElements = SerializeInstanceFields( heap.cls, heap.fields, depth + 1 );
			}
#if UNITY_EDITOR
			else if( hasCilboxable && !( fv is MonoBehaviour ) )
			{
				SerializeNativePlainObject( spf, fv, refToProxyMap, depth );
			}
#endif
			// Not a proxiable script.  (At runtime there is no map, and references to proxies are UnityEngine.Objects.)
			else if (hasCilboxable && refToProxyMap != null)
			{
				spf.fieldType = (byte)ProxyFieldType.CilboxRef;
				spf.fieldObjectIndex = fieldsObjects.Count;
				spf.objectRefName = fv.ToString();
				spf.objectRefIsNull = spf.objectRefName == "null";
				fieldsObjects.Add( refToProxyMap[(MonoBehaviour)fv] );
			}
			else if( fv is UnityEngine.Object )
			{
				spf.fieldType = (byte)ProxyFieldType.ObjectRef;
				spf.fieldObjectIndex = fieldsObjects.Count;
				spf.objectRefName = fv.ToString();
				spf.objectRefIsNull = spf.objectRefName == "null";
				fieldsObjects.Add( (UnityEngine.Object)fv );
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
				// Arrays of Cilbox classes are object[] (or CilboxProxy[]) at runtime; only the declared type names the class.
				bool useDeclared = declaredArray && ( type == typeof(object) || type == typeof(CilboxProxy) );
				spf.elementType = useDeclared ? declaredElementType : SerializedTypeDescriptorBuilder.FromNativeType( type );
				Array arr = (Array)fv;
				int len = arr.Length;
				spf.arrayElements = new SerializedProxyField[len];
				for( int i = 0; i < len; i++ )
					spf.arrayElements[i] = SerializeProxyField( arr.GetValue(i), null, -1, declaredElementType, refToProxyMap, depth, ownerClassName );
			}
			else
			{
				spf.fieldType = (byte)ProxyFieldType.Json;
				spf.data = JsonUtility.ToJson(fv);
				spf.elementType = SerializedTypeDescriptorBuilder.FromNativeType( fvType );
			}

			return spf;
		}

		// Unity serializes the live object when it is cloned (Object.Instantiate).  Re-serialize the current field
		// values into the same payload SetupProxy produces, and the clone loads them through RuntimeProxyLoad().
		public void OnBeforeSerialize()
		{
			// Not loaded yet: the payload from setup is still the current state.
			if( !proxyWasSetup )
				return;

			fieldsObjects = new List< UnityEngine.Object >();
			serializedObjectData = new SerializedProxy
			{
				fields = SerializeInstanceFields( cls, fields, 0 ),
			}.SerializeString();
		}

		public void OnAfterDeserialize() { }

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
			if (string.IsNullOrEmpty(serializedObjectData)) return;
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

				runtimeFieldsObjects = fieldsObjects != null ? new List<UnityEngine.Object>(fieldsObjects) : new List<UnityEngine.Object>();

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

				SerializedProxy proxyData = SerializedProxy.DeserializeString( serializedObjectData );

				SerializedProxyField[] matchingProxyField = new SerializedProxyField[fieldCount];
				foreach( SerializedProxyField spf in proxyData.fields )
				{
					// Go over the root objects, to see which ones slot in and how.  Empty ones are kept: a
					// serialized null replaces the constructor's value, as in Unity.
					if( spf.matchingInstanceId >= 0 &&
						spf.matchingInstanceId < matchingProxyField.Length )
					{
						matchingProxyField[spf.matchingInstanceId] = spf;
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
					SerializedProxyField spf = matchingProxyField[i];

					if( spf == null ) { /* Debug.Log( $"Skipping {i} {cls.instanceFieldNames[i]}" ); */ continue; }

					if( (ProxyFieldType)spf.fieldType == ProxyFieldType.Primitive )
					{
						spf.primitiveValue.ToStackElement(ref fields[i]);
						continue;
					}

					LoadProxyFieldStackElement( spf, ref fields[i], cls.instanceFieldNames[i] );
				}


				proxyWasSetup = true;
				runtimeFieldsObjects = null;
				serializedObjectData = null;
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
				CilboxClass heapClass = box.GetClass( spf.data );
				if( heapClass == null )
				{
					Debug.LogWarning( $"[CilboxProxy:{gameObject.name}] Unknown class {spf.data} for field {rootFieldName}." );
					break;
				}

				// Like Unity: construct the object, then overwrite its serialized fields.
				CilboxHeapInstance heap = heapClass.CreateDefaultInstance();
				heapClass.InterpretMethod( heap, ImportFunctionID.dotCtor, null );
				foreach( SerializedProxyField child in spf.arrayElements )
				{
					int index = child.matchingInstanceId;
					if( index < 0 || index >= heap.fields.Length )
						continue;
					LoadProxyFieldStackElement( child, ref heap.fields[index], rootFieldName + "." + heapClass.instanceFieldNames[index] );
				}

				// Unity's null normalization also applies to what is still null after construction (a fresh instance has no
				// serialized fields to load): empty strings, arrays and lists, and fresh instances of plain classes.
				if( heapLoadDepth < MaxSerializeDepth )
				{
					heapLoadDepth++;
					try
					{
						for( int index = 0; index < heap.fields.Length; index++ )
						{
							if( !heapClass.IsInstanceFieldSerialized( index ) || heap.fields[index].type != StackType.Object || heap.fields[index].o != null )
								continue;
							SerializedProxyField normalized = SerializeProxyField( null, heapClass.instanceFieldNames[index], index, heapClass.instanceFieldRows[index].type, null, 0, heapClass.className );
							LoadProxyFieldStackElement( normalized, ref heap.fields[index], rootFieldName + "." + heapClass.instanceFieldNames[index] );
						}
					}
					finally
					{
						heapLoadDepth--;
					}
				}
				refElement.LoadObject( heap );
				return;
			}

			case ProxyFieldType.Array:
			{
				Type t = box.usage.GetNativeTypeFromDescriptor( spf.elementType );
				bool isCilboxElementType = false;

				if (t == null)
				{
					if (box.classes.ContainsKey(spf.elementType.typeName))
					{
						// Check the array to see if it is Cilboxed
						t = typeof(CilboxProxy);
						isCilboxElementType = true;

						// Arrays of plain Cilbox classes hold CilboxHeapInstances, which a CilboxProxy[] cannot.
						if( Array.Exists( spf.arrayElements, e => (ProxyFieldType)e.fieldType == ProxyFieldType.CilboxHeap ) )
							t = typeof(object);
					}
					else // type is null and not a Cilbox class
					{
						refElement.LoadObject(null);
						return;
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
						StackElement temp = default;
						LoadProxyFieldStackElement( spf.arrayElements[j], ref temp, rootFieldName );
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

			case ProxyFieldType.Empty:
			default:
				break;
			}

			refElement.LoadObject(null);
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

