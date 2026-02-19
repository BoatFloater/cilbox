using UnityEngine;
using Cilbox;
using System.Collections.Generic;
using System;
using System.Collections.Specialized;
using System.Collections;
using System.Runtime.InteropServices;
using System.Reflection;
using TestCilbox;

namespace TestCilbox
{
	[Cilboxable]
	public class TestCilboxBehaviour : MonoBehaviour
	{
		private int ipriviateinance = 555;
		public int ipublicinstance = 556;
		static private int iprivatestatic = 557;
		static public int ipublicstatic = 558;
		static public int recursive_test_counter = 0;
		public TestCilboxBehaviour2 behaviour2;
		public TestCilboxBehaviour() { }

		public void RecursiveTest(int i)
		{
			recursive_test_counter++;
			if( i <= 0 ) return;
			RecursiveTest( i-1 );
			RecursiveTest( i-1 );
		}

		public void Start()
		{
			Debug.Log( "🔵 TestCilboxBehaviour.Start()" );

			Validator.Set( "Start Test", "OK" );

			int i = (int)(((int)(5+2/3)+50)*2/7.3);
			Validator.Set( "Arithmatic Test", i.ToString() );

			String startMarks = Validator.Get("Start Marks");
			if( startMarks == null ) startMarks = "";
			Validator.Set( "Start Marks", startMarks + "I" );

			Validator.Set( "private instance filed", ipriviateinance.ToString() );
			Validator.Set( "public instance field", ipublicinstance.ToString() );
			Validator.Set( "private static field", iprivatestatic.ToString() );
			Validator.Set( "public static field", ipublicstatic.ToString() );

			RecursiveTest(8);
			Validator.Set( "recursive function", recursive_test_counter.ToString() );
			Validator.Set( "string concatenation", "it" + " " + "works" );
			Validator.Set( "MathF.Sin", MathF.Sin(3.2f).ToString() );

			Vector3 testVec = new Vector3(7, 7, 7);
			testVec.x += 5;
			float myX = testVec.x;
			Debug.Log("My X: " + myX );

			int myInt = 14;
			ReadInt(ref myInt);
			ReadFloat(ref testVec.y);
			WriteInt(ref myInt, 42);
			WriteFloat(ref testVec.y, 42.0f);
			Debug.Log("New derp: " + myInt );
			Debug.Log("New testVec.y: " + testVec.y );

			behaviour2.Behaviour2Test();
		}

		public void Update()
		{
			Validator.Set( "Update", "called" );
			Validator.Set( "Overtime", "timed out" );
			double result = 1.3;
			for( int i = 0; i < 10000000; i++ ) result = System.Math.Sin( result ) * 10.0;
			Validator.Set( "Throwaway", result.ToString() );
			Validator.Set( "Overtime", "did not timed out" );
		}


		public void FixedUpdate()
		{
			Validator.Set( "Execution after timeout", "enabled" );
			Validator.Set( "Manual Recover After Timeout", "recovered" );
			Validator.Set( "FixedUpdate", "called" );
		}

		public void ReadInt(ref int field)
		{
			int current = field;
			Debug.Log("ReadInt: " + current);
		}

		public void WriteInt(ref int field, int value)
		{
			Debug.Log("WriteInt: " + value);
			field = value;
		}

		public void ReadFloat(ref float field)
		{
			float current = field;
			Debug.Log("ReadFloat: " + current);
		}

		public void WriteFloat(ref float field, float value)
		{
			Debug.Log("WriteFloat: " + value);
			field = value;
		}
	}


	[Cilboxable]
	public class TestCilboxBehaviour2 : MonoBehaviour
	{
		public int pubsettee = 35254;
		public void Behaviour2Test()
		{
			Validator.Set( "Method Called On Peer", "OK" );
			Validator.Set( "Public Field Change In Editor", pubsettee.ToString() ); // Should not be 35254
		}
	}
}

