using System;

namespace CoreCalc.Types {
	/// <summary>
	/// A NotImplementedException signals that something could have 
	/// been implemented but was not.
	/// </summary>
	internal class NotImplementedException : Exception {
		public NotImplementedException(String msg) : base(msg) { }
	}
}