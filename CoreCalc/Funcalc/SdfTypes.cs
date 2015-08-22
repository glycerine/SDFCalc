﻿// Funcalc, spreadsheet with functions
// ----------------------------------------------------------------------
// Copyright (c) 2006-2014 Peter Sestoft

// Permission is hereby granted, free of charge, to any person
// obtaining a copy of this software and associated documentation
// files (the "Software"), to deal in the Software without
// restriction, including without limitation the rights to use, copy,
// modify, merge, publish, distribute, sublicense, and/or sell copies
// of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:

//  * The above copyright notice and this permission notice shall be
//    included in all copies or substantial portions of the Software.

//  * The software is provided "as is", without warranty of any kind,
//    express or implied, including but not limited to the warranties of
//    merchantability, fitness for a particular purpose and
//    noninfringement.  In no event shall the authors or copyright
//    holders be liable for any claim, damages or other liability,
//    whether in an action of contract, tort or otherwise, arising from,
//    out of or in connection with the software or the use or other
//    dealings in the software.
// ----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Text;
using System.Reflection;
using System.Reflection.Emit;
using System.Linq;

namespace Corecalc.Funcalc {
  /// <summary>
  /// An SdfType represents and parses signatures of sheet-defined functions
  /// as well as .NET Class Library (external) methods.
  /// </summary>
  public abstract class SdfType {
    public abstract Type GetDotNetType();

    /// <summary>
    /// A SigToken is a lexical used in the signature of an external function.
    /// </summary>
    private abstract class SigToken {
      public readonly static SigToken
        LPAR = new LPar(),
        RPAR = new RPar(),
        LBRK = new LBrk(),
        LBRE = new LBre(),
        EOF = new Eof();
    }

    private class LPar : SigToken { }   // (
    private class RPar : SigToken { }   // )
    private class LBrk : SigToken { }   // [
    private class LBre : SigToken { }   // {
    private class Eof  : SigToken { }

    /// <summary>
    /// A TypenameToken represents a type name in a signature.
    /// </summary>
    private class TypenameToken : SigToken {
      public readonly String typename;
      public TypenameToken(String typename) {
        this.typename = typename;
      }
      public override string ToString() {
        return typename;
      }
    }

    /// <summary>
    /// An ErrorToken represents an error in lexical analysis of a signature.
    /// </summary>
    private class ErrorToken : SigToken {
      public readonly String message;
      public ErrorToken(String message) {
        this.message = message;
      }
    }

    // ParseType: from stream of signature tokens to a Type object.

    public static SdfType ParseType(String signature) {
      IEnumerator<SigToken> tokens = Scanner(signature);
      tokens.MoveNext();  // There's at least Eof in the stream
      SdfType res = ParseOneType(tokens);
      if (tokens.Current is Eof)
        return res;
      else
        throw new SigParseException("Extraneous characters in signature");
    }

    // Before the call, tokens.Current is the first token of the type 
    // to parse; and after the call it is the first token after that type.

    private static SdfType ParseOneType(IEnumerator<SigToken> tokens) {
      if (tokens.Current is LPar) {
        tokens.MoveNext();
        return ParseFunctionSignature(tokens);
      } else if (tokens.Current is LBrk) {
        tokens.MoveNext();
        return ParseArraySignature(tokens, 1);
      } else if (tokens.Current is LBre) {
        tokens.MoveNext();
        return ParseArraySignature(tokens, 2);
      } else if (tokens.Current is TypenameToken) {
        TypenameToken token = tokens.Current as TypenameToken;
        tokens.MoveNext();
        switch (token.typename) {
          case "Z": return new SimpleType(typeof(System.Boolean));
          case "C": return new SimpleType(typeof(System.Char));
          case "B": return new SimpleType(typeof(System.SByte));
          case "b": return new SimpleType(typeof(System.Byte));
          case "S": return new SimpleType(typeof(System.Int16));
          case "s": return new SimpleType(typeof(System.UInt16));
          case "I": return new SimpleType(typeof(System.Int32));
          case "i": return new SimpleType(typeof(System.UInt32));
          case "J": return new SimpleType(typeof(System.Int64));
          case "j": return new SimpleType(typeof(System.UInt64));
          case "F": return new SimpleType(typeof(System.Single));
          case "D":
          case "N": return new SimpleType(typeof(System.Double));
          case "M": return new SimpleType(typeof(System.Decimal));
          case "V": return new SimpleType(Value.type);
          case "W": return new SimpleType(typeof(void));
          case "T": return new SimpleType(typeof(System.String));
          case "O": return new SimpleType(typeof(System.Object));
          default:
            return new SimpleType(ExternalFunction.FindType(token.typename));
        }
      } else if (tokens.Current is Eof)
        throw new SigParseException("Unexpected end of signature");
      else
        throw new SigParseException("Unexpected token " + tokens.Current);
    }

    private static SdfType ParseFunctionSignature(IEnumerator<SigToken> tokens) {
      List<SdfType> arguments = new List<SdfType>();
      while (!(tokens.Current is Eof) && !(tokens.Current is RPar))
        arguments.Add(ParseOneType(tokens));
      if (tokens.Current is RPar)
        tokens.MoveNext();
      else
        throw new SigParseException("Unexpected end of function signature");
      SdfType returntype = ParseOneType(tokens);
      return new FunctionType(arguments.ToArray(), returntype);
    }

    private static SdfType ParseArraySignature(IEnumerator<SigToken> tokens, int dim) {
      if (tokens.Current is Eof)
        throw new SigParseException("Unexpected end of function signature");
      SdfType elementtype = ParseOneType(tokens);
      return new ArrayType(elementtype, dim);
    }

    /// <summary>
    /// A SigParseException signals an error during parsing of a function signature.
    /// </summary>
    public class SigParseException : Exception {
      public SigParseException(String message) : base(message) { }
    }

    // Scanner: from signature string to stream of signature tokens

    private static IEnumerator<SigToken> Scanner(String signature) {
      int i = 0;
      while (i < signature.Length) {
        char ch = signature[i];
        switch (ch) {
          case 'Z':
          case 'C':
          case 'B':
          case 'b':
          case 'S':
          case 's':
          case 'I':
          case 'i':
          case 'J':
          case 'j':
          case 'D':
          case 'N':
          case 'F':
          case 'M':
          case 'V':
          case 'W':
          case 'T':
          case 'O':
            yield return new TypenameToken(signature.Substring(i, 1));
            break;
          case 'L': // For instance, LSystem.Text.StringBuilder;
            i++;
            int start = i;
            while (i < signature.Length && signature[i] != ';')
              i++;
            // Now signature[i]==';' or i == signature.Length
            if (i < signature.Length)
              yield return new TypenameToken(signature.Substring(start, i - start));
            else
              yield return new ErrorToken("Unterminated class name");
            break;
          case '(':
            yield return SigToken.LPAR;
            break;
          case ')':
            yield return SigToken.RPAR;
            break;
          case '[':
            yield return SigToken.LBRK;
            break;
          case '{':
            yield return SigToken.LBRE;
            break;
          default:
            yield return new ErrorToken("Illegal character '" + ch + "'");
            break;
        }
        i++;
      }
      yield return SigToken.EOF;
      yield break;
    }
  }


  /// <summary>
  /// A SimpleType is simple (non-composite) type such as Double, String, StringBuilder.
  /// </summary>
  public class SimpleType : SdfType {
    public readonly Type type;

    public SimpleType(Type type) {
      this.type = type;
    }

    public override Type GetDotNetType() {
      return type;
    }

    public override string ToString() {
      return type.ToString();
    }
  }

  /// <summary>
  /// A FunctionType is the type of a sheet-defined function, 
  /// such as Number * Text -> Value.
  /// </summary>
  public class FunctionType : SdfType {
    public readonly SdfType[] arguments;
    public readonly SdfType returntype;

    public FunctionType(SdfType[] arguments, SdfType returntype) {
      this.arguments = arguments;
      this.returntype = returntype;
    }

    public Type[] ArgumentDotNetTypes() {
      Type[] res = new Type[arguments.Length];
      for (int i = 0; i < arguments.Length; i++)
        res[i] = arguments[i].GetDotNetType();
      return res;
    }

    public override Type GetDotNetType() {
      throw new Exception("Function type not allowed");
    }

    public override String ToString() {
      StringBuilder sb = new StringBuilder();
      sb.Append("(");
      if (arguments.Length > 0)
        sb.Append(arguments[0]);
      for (int i = 1; i < arguments.Length; i++)
        sb.Append(" * ").Append(arguments[i]);
      sb.Append(" -> ").Append(returntype).Append(")");
      return sb.ToString();
    }
  }

  /// <summary>
  /// An ArrayType is a CLI array type such as String[] or String[,] 
  /// or a spreadsheet array type such as Number array.
  /// </summary>
  public class ArrayType : SdfType {
    public readonly SdfType elementtype;
    public readonly int dim;

    public ArrayType(SdfType elementtype, int dim) {
      this.elementtype = elementtype;
      this.dim = dim;
    }

    public override Type GetDotNetType() {
      if (dim == 1) // Special case, see System.Type.MakeArrayType(int)
        return elementtype.GetDotNetType().MakeArrayType();
      else
        return elementtype.GetDotNetType().MakeArrayType(dim);
    }

    public override String ToString() {
      return elementtype + (dim == 1 ? "[]" : dim == 2 ? "[,]" : "UNHANDLED");
    }
  }

  /// <summary>
  /// An ExternalFunction represents an external .NET function, conversion 
  /// of its arguments from spreadsheet Values to .NET values, conversion of 
  /// its result value in the opposite direction, its .NET MethodInfo for 
  /// invoking it, its .NET signature, and more.
  /// </summary>
  class ExternalFunction {
    private readonly Func<Value, Object>[] argConverters;
    private readonly Func<Object, Value> resConverter;
    private readonly MethodBase mcInfo;     // MethodInfo or ConstructorInfo
    private readonly Type[] argTypes;       // Does not include receiver type
    private readonly Object[] argValues;    // Reused from call to call
    private readonly Type resType;          // Result type
    private readonly bool isStatic;
    private readonly Type recType;                      // Receiver type
    private readonly Func<Value, Object> recConverter;  // Receiver converter 
    public readonly int arity;              // Includes receiver if !isStatic

    // Invariant: argTypes.Length == argConverters.Length == argValues.Length
    // Invariant: if isStatic then arity==argTypes.Length else arity==argTypes.Length+1
    // Invariant: if !isStatic then recType != null and recConverter != null
    // Invariant: argConverters[i] == toObjectConverter[argTypes[i]]
    // Invariant: recConverter == null || recConverter = toObjectConverter[recType]
    // Invariant: resConverter == fromObjectConverter[resType]

    // Mapping .NET types to argument and result converters: Value <-> Object
    private static readonly IDictionary<Type, Func<Value, Object>> toObjectConverter
      = new Dictionary<Type, Func<Value, Object>>();
    private static IDictionary<Type, Func<Object, Value>> fromObjectConverter
      = new Dictionary<Type, Func<Object, Value>>();

    static ExternalFunction() {
      // Initialize tables of conversions
      // From Funcalc type to .NET type, for argument converters
      toObjectConverter.Add(typeof(System.Int64), NumberValue.ToInt64);
      toObjectConverter.Add(typeof(System.Int32), NumberValue.ToInt32);
      toObjectConverter.Add(typeof(System.Int16), NumberValue.ToInt16);
      toObjectConverter.Add(typeof(System.SByte), NumberValue.ToSByte);
      toObjectConverter.Add(typeof(System.UInt64), NumberValue.ToUInt64);
      toObjectConverter.Add(typeof(System.UInt32), NumberValue.ToUInt32);
      toObjectConverter.Add(typeof(System.UInt16), NumberValue.ToUInt16);
      toObjectConverter.Add(typeof(System.Byte), NumberValue.ToByte);
      toObjectConverter.Add(typeof(System.Double), NumberValue.ToDouble);
      toObjectConverter.Add(typeof(System.Single), NumberValue.ToSingle);
      toObjectConverter.Add(typeof(System.Boolean), NumberValue.ToBoolean);
      toObjectConverter.Add(typeof(System.String), TextValue.ToString);
      toObjectConverter.Add(typeof(System.Char),   TextValue.ToChar);
      toObjectConverter.Add(typeof(System.Object), Value.ToObject);
      toObjectConverter.Add(typeof(System.Double[]), ArrayValue.ToDoubleArray1D);
      toObjectConverter.Add(typeof(System.Double[,]), ArrayValue.ToDoubleArray2D);
      toObjectConverter.Add(typeof(System.String[]), ArrayValue.ToStringArray1D);

      // From .NET type to Funcalc type, for result converters
      fromObjectConverter.Add(typeof(System.Int64), NumberValue.FromInt64);
      fromObjectConverter.Add(typeof(System.Int32), NumberValue.FromInt32);
      fromObjectConverter.Add(typeof(System.Int16), NumberValue.FromInt16);
      fromObjectConverter.Add(typeof(System.SByte), NumberValue.FromSByte);
      fromObjectConverter.Add(typeof(System.UInt64), NumberValue.FromUInt64);
      fromObjectConverter.Add(typeof(System.UInt32), NumberValue.FromUInt32);
      fromObjectConverter.Add(typeof(System.UInt16), NumberValue.FromUInt16);
      fromObjectConverter.Add(typeof(System.Byte), NumberValue.FromByte);
      fromObjectConverter.Add(typeof(System.Double), NumberValue.FromDouble);
      fromObjectConverter.Add(typeof(System.Single), NumberValue.FromSingle);
      fromObjectConverter.Add(typeof(System.Boolean), NumberValue.FromBoolean);
      fromObjectConverter.Add(typeof(System.String), TextValue.FromString);
      fromObjectConverter.Add(typeof(System.Char),   TextValue.FromChar);
      fromObjectConverter.Add(typeof(System.Object), ObjectValue.Make);
      fromObjectConverter.Add(typeof(System.String[]), ArrayValue.FromStringArray1D);
      fromObjectConverter.Add(typeof(System.Double[]), ArrayValue.FromDoubleArray1D);
      fromObjectConverter.Add(typeof(System.Double[,]), ArrayValue.FromDoubleArray2D);
      fromObjectConverter.Add(typeof(void), Value.MakeVoid);
    }

    // For caching the result of nameAndSignature lookups
    private static readonly IDictionary<String, ExternalFunction> cache
     = new Dictionary<String, ExternalFunction>();

    public static ExternalFunction Make(String nameAndSignature) {
      ExternalFunction res;
      if (!cache.TryGetValue(nameAndSignature, out res)) {
        res = new ExternalFunction(nameAndSignature);
        cache.Add(nameAndSignature, res);
      }
      return res;
    }

    private ExternalFunction(String nameAndSignature) {
      int firstParen = nameAndSignature.IndexOf('(');
      if (firstParen <= 0 || firstParen == nameAndSignature.Length - 1)
        throw new Exception("#ERR: Ill-formed name and signature");
      isStatic = nameAndSignature[firstParen - 1] == '$';
      String name = nameAndSignature.Substring(0, isStatic ? firstParen - 1 : firstParen);
      String signature = nameAndSignature.Substring(firstParen);
      int lastDot = name.LastIndexOf('.');
      if (lastDot <= 0 || lastDot == name.Length - 1)
        throw new Exception("#ERR: Ill-formed .NET method name");
      String typeName = name.Substring(0, lastDot);
      String methodName = name.Substring(lastDot + 1);
      // Experimental: Search appdomain's assemblies
      Type declaringType = FindType(typeName);
      if (declaringType == null)
        throw new Exception("#ERR: Unknown .NET type " + typeName);
      FunctionType ft = SdfType.ParseType(signature) as FunctionType;
      if (ft == null)
        throw new Exception("#ERR: Ill-formed .NET method signature");
      argTypes = ft.ArgumentDotNetTypes();
      resType = ft.returntype.GetDotNetType();
      if (methodName == "new" && isStatic)
        mcInfo = declaringType.GetConstructor(argTypes);
      else
        mcInfo = declaringType.GetMethod(methodName, argTypes);
      if (mcInfo == null)
        throw new Exception("#ERR: Unknown .NET method");
      argConverters = new Func<Value, Object>[argTypes.Length];
      for (int i = 0; i < argTypes.Length; i++)
        argConverters[i] = GetToObjectConverter(argTypes[i]);
      resConverter = GetFromObjectConverter(ft.returntype.GetDotNetType());
      if (isStatic)
        arity = argTypes.Length;
      else {
        arity = argTypes.Length + 1;
        recType = declaringType;
        recConverter = GetToObjectConverter(recType);
      }
      argValues = new Object[argTypes.Length];   // Allocate once, reuse at calls
    }

    private static Func<Value, Object> GetToObjectConverter(Type typ) {
      if (toObjectConverter.ContainsKey(typ))
        return toObjectConverter[typ];
      else
        return ObjectValue.ToObject;
        //return delegate(Value v) {
        //  ObjectValue o = v as ObjectValue;
        //  return o != null && typ.IsInstanceOfType(o.value) ? o.value : null;
        //};
    }

    private static Func<Object, Value> GetFromObjectConverter(Type typ) {
      if (fromObjectConverter.ContainsKey(typ))
        return fromObjectConverter[typ];
      else
        return ObjectValue.Make;
        //return delegate(Object o) {
        //  return typ.IsInstanceOfType(o) ? ObjectValue.Make(o) : ErrorValue.argTypeError;
        //};
    }

    // Experimental: Search for type by name in some known assemblies

    private static readonly String[] assmNames = new String[] {
      "mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089",
      "System, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089",
      // This is for testing EXTERN functions:
      "Externals, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"
    };

    public static Type FindType(String typeName) {
      Type declaringType = null;
      foreach (String assmName in assmNames) {
        Assembly assm = Assembly.Load(assmName);
        // Console.WriteLine(assm);
        declaringType = assm.GetType(typeName);
        if (declaringType != null)
          break;
      }
      return declaringType;
    }

    // Called from the EXTERN function applier in interpreted sheets.

    public Value Call(Value[] vs) {
      if (vs.Length != arity)
        return ErrorValue.argCountError;
      Object receiver;
      if (isStatic) {
        receiver = null;
        for (int i = 0; i < vs.Length; i++)
          argValues[i] = argConverters[i](vs[i]);
      } else {
        receiver = recConverter(vs[0]);
        for (int i = 1; i < vs.Length; i++)
          argValues[i - 1] = argConverters[i - 1](vs[i]);
      }
      if (mcInfo is ConstructorInfo)
        return resConverter((mcInfo as ConstructorInfo).Invoke(argValues));
      else
        return resConverter(mcInfo.Invoke(receiver, argValues));
    }

    // These are used by the SDF code generator in CGExtern

    public void EmitCall(ILGenerator ilg) {
      if (mcInfo is ConstructorInfo)
        ilg.Emit(OpCodes.Newobj, mcInfo as ConstructorInfo);
      else if (isStatic)
        ilg.Emit(OpCodes.Call, mcInfo as MethodInfo);
      else 
        ilg.Emit(OpCodes.Call, mcInfo as MethodInfo);
    }

    public Type ArgType(int i) {
      return isStatic ? argTypes[i] : i > 0 ? argTypes[i - 1] : recType;
    }

    public Func<Value, Object> ArgConverter(int i) {
      return isStatic ? argConverters[i] : i > 0 ? argConverters[i - 1] : recConverter;
    }

    public Type ResType {
      get { return resType; }
    }

    public Func<Object, Value> ResConverter {
      get { return resConverter; }
    }
  }
}