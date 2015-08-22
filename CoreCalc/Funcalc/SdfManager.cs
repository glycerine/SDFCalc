// Funcalc, spreadsheet with functions
// ----------------------------------------------------------------------
// Copyright (c) 2006-2014 Peter Sestoft and others

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
using System.Diagnostics;
using System.Linq;

namespace Corecalc.Funcalc {
  /// <summary>
  /// SfdManager holds the catalog of sheet defined functions.
  /// </summary>
  static class SdfManager {
    // Static tree dictionary to map an SDF name to an SdfInfo object.
    private static readonly IDictionary<String, SdfInfo> sdfNameToInfo
      = new SortedDictionary<String, SdfInfo>();
    // Static arrays to map an SDF index to a callable SdfDelegate and SdfInfo.
    // Poorer modularity than if we used a private ArrayList, but faster SDF calls.
    // Invariant: sdfInfos[i]==null || sdfInfos[i].index==i
    // Invariant: sdfDelegates.Length == sdfInfos.Length >= 1
    public static Delegate[] sdfDelegates = new Delegate[1];
    private static SdfInfo[] sdfInfos = new SdfInfo[1];
    private static int nextIndex = 0;

    // For generating code to call an SDF, in CGSdfCall and CGApply
    internal static readonly FieldInfo sdfDelegatesField
      = typeof(SdfManager).GetField("sdfDelegates");

    // For discovering edits to sheet-defined functions, and for coloring them
    public static readonly CellsUsedInFunctions cellToFunctionMapper
      = new CellsUsedInFunctions();

    // For caching partially evaluated sheet-defined functions
    private static readonly IDictionary<FunctionValue, SdfInfo> specializations
      = new Dictionary<FunctionValue, SdfInfo>();

    public static void ResetTables() {
      foreach (SdfInfo info in sdfNameToInfo.Values)
        Function.Remove(info.name);
      sdfNameToInfo.Clear();
      sdfDelegates = new Delegate[1];
      sdfInfos = new SdfInfo[1];
      nextIndex = 0;
      cellToFunctionMapper.Clear();
      specializations.Clear();
    }

    public static void ShowIL(SdfInfo info) {
      MethodInfo mif = sdfDelegates[info.index].Method;
      ClrTest.Reflection.MethodBodyViewer viewer = new ClrTest.Reflection.MethodBodyViewer();
      viewer.Text = info.ToString();
      viewer.SetMethodBase(mif);
      viewer.ShowDialog();
    }

    public static void CreateFunction(string name, FullCellAddr outPutCell, List<FullCellAddr> inputCells) {
      CreateFunction(name, outPutCell, inputCells.ToArray());
    }

    public static void CreateFunction(string name, FullCellAddr outputCell, FullCellAddr[] inputCells) {
      name = name.ToUpper();
      // If the function exists, with the same input and output cells, keep it.
      // If it is a placeholder, overwrite its applier; if its input and output
      // cells have changed, recreate it (including its SdfInfo record).
      Function oldFunction = Function.Get(name);
      if (oldFunction != null) 
        if (!oldFunction.IsPlaceHolder)
          return;
      // Registering the function before compilation allows it to call itself recursively
      SdfInfo sdfInfo = Register(outputCell, inputCells, name);
      // Console.WriteLine("Compiling {0} as #{1}", name, info.index);
      Update(sdfInfo, CompileSdf(sdfInfo));
      if (oldFunction != null) // ... and is not a placeholder
        oldFunction.UpdateApplier(sdfInfo.Apply, sdfInfo.IsVolatile);
      else
        new Function(name, sdfInfo.Apply, isVolatile: sdfInfo.IsVolatile);
    }

    /// <summary>
    /// Compiles entry code and body of a sheet-defined function
    /// </summary>
    /// <param name="info">The SdfInfo object describing the function</param>
    /// <returns></returns>
    private static Delegate CompileSdf(SdfInfo info) {
      // Build dependency graph containing all cells needed by the output cell
      DependencyGraph dpGraph = new DependencyGraph(info.outputCell, info.inputCells,
        delegate(FullCellAddr fca) { return fca.sheet[fca.ca]; });
      // Topologically sort the graph in calculation order; leave out constants
      IList<FullCellAddr> cellList = dpGraph.PrecedentOrder();
      info.SetVolatility(cellList);
      // Convert each Expr into a CGExpr while preserving order.  Inline single-use expressions
      cellToFunctionMapper.AddFunction(info, dpGraph.GetAllNodes());
      return ProgramLines.CreateSdfDelegate(info, dpGraph, cellList);  
    }

    /// <summary>
    /// Create a residual (sheet-defined) function for the given FunctionValue.  
    /// As a side effect, register the new SDF and cache it in a dictionary mapping
    /// function values to residual SDFs.
    /// </summary>
    /// <param name="fv">The function value to specialize</param>
    /// <returns></returns>
    public static SdfInfo SpecializeAndCompile(FunctionValue fv) {
      SdfInfo residualSdf;
      if (!specializations.TryGetValue(fv, out residualSdf)) {
        FullCellAddr[] residualInputCells = fv.sdfInfo.Program.ResidualInputs(fv);
        String name = String.Format("{0}#{1}", fv, nextIndex);
        Console.WriteLine("Created residual function {0}", name);
        // Register before partial evaluation to enable creation of call cycles
        residualSdf = Register(fv.sdfInfo.outputCell, residualInputCells, name);
        specializations.Add(fv, residualSdf);
        ProgramLines residual = fv.sdfInfo.Program.PEval(fv.args, residualInputCells);
        residualSdf.Program = residual;
        Update(residualSdf, residual.CompileToDelegate(residualSdf));
      }
      return residualSdf;
    }

    // TODO: This may be inefficient when we have many specialized functions.
    // Perhaps maintain a more specialized data structure, such as a dictionary 
    // from function name to dictionary from value list to specialization.
    public static List<Value[]> PendingSpecializations(String name) {
      List<Value[]> result = new List<Value[]>();
      foreach (FunctionValue fv in specializations.Keys)
        // TODO: Should we only consider pending (fv.sdfInfo.Program==null) functions?
        if (fv.sdfInfo.name == name)
          result.Add(fv.args);
      // Recipient is expected not to update the Value arrays
      return result;
    }

    // Register, unregister, update and look up the SDF tables

    /// <summary>
    /// Allocate an index for a new SDF, but do not bind its SdfDelegate
    /// </summary>
    /// <param name="outputCell"></param>
    /// <param name="inputCells"></param>
    /// <param name="name"></param>
    /// <returns></returns>
    public static SdfInfo Register(FullCellAddr outputCell, FullCellAddr[] inputCells, string name) {
      name = name.ToUpper();
      SdfInfo sdfInfo = GetInfo(name);
      if (sdfInfo == null) { // New SDF, register it
        sdfInfo = new SdfInfo(outputCell, inputCells, name, nextIndex++);
        Debug.Assert(sdfInfo.index == nextIndex - 1);
        sdfNameToInfo[name] = sdfInfo;
        if (sdfInfo.index >= sdfDelegates.Length) {
          Debug.Assert(sdfDelegates.Length == sdfInfos.Length);
          // Reallocate sdfDelegates array
          Delegate[] newSdfs = new Delegate[2 * sdfDelegates.Length];
          Array.Copy(sdfDelegates, newSdfs, sdfDelegates.Length);
          sdfDelegates = newSdfs;
          // Reallocate sdfInfos array
          SdfInfo[] newSdfInfos = new SdfInfo[2 * sdfInfos.Length];
          Array.Copy(sdfInfos, newSdfInfos, sdfInfos.Length);
          sdfInfos = newSdfInfos;
        }
        sdfInfos[sdfInfo.index] = sdfInfo;
        // Update SDF function listbox if created and visible 
        GUI.SdfForm sdfForm = System.Windows.Forms.Application.OpenForms["sdf"] as GUI.SdfForm;
        if (sdfForm != null && sdfForm.Visible) {
          sdfForm.PopulateFunctionListBox(false);
          sdfForm.PopulateFunctionListBox(name);
          sdfForm.Invalidate();
        }
      }
      return sdfInfo;
    }

    private static void Update(SdfInfo info, Delegate method) {
      sdfDelegates[info.index] = method;
    }

    private static readonly ErrorValue errorDeleted
      = ErrorValue.Make("#FUNERR: Function deleted");
    private static readonly Delegate[] sdfDeleted
      = { (Func<Value>)(delegate { return errorDeleted; }),
          (Func<Value,Value>)(delegate { return errorDeleted; }),
          (Func<Value,Value,Value>)(delegate { return errorDeleted; }),
          (Func<Value,Value,Value,Value>)(delegate { return errorDeleted; }),
          (Func<Value,Value,Value,Value,Value>)(delegate { return errorDeleted; }),
          (Func<Value,Value,Value,Value,Value,Value>)(delegate { return errorDeleted; }),
          (Func<Value,Value,Value,Value,Value,Value,Value>)(delegate { return errorDeleted; }),
          (Func<Value,Value,Value,Value,Value,Value,Value,Value>)(delegate { return errorDeleted; }),
          (Func<Value,Value,Value,Value,Value,Value,Value,Value,Value>)(delegate { return errorDeleted; }),
          (Func<Value,Value,Value,Value,Value,Value,Value,Value,Value,Value>)(delegate { return errorDeleted; }),
        };

    private static void Unregister(SdfInfo info) {
      sdfNameToInfo.Remove(info.name);
      sdfDelegates[info.index] = sdfDeleted[info.arity];
      sdfInfos[info.index] = null;
    }

    public static SdfInfo GetInfo(String name) {
      SdfInfo info;
      if (sdfNameToInfo.TryGetValue(name.ToUpper(), out info))
        return info;
      else
        return null;
    }

    public static SdfInfo GetInfo(int sdfIndex) {
      return sdfInfos[sdfIndex];
    }

    public static IEnumerable<SdfInfo> GetAllInfos() {
      return sdfNameToInfo.Values;
    }

    /// <summary>
    /// Regenerates the indicated SDF delegates
    /// </summary>
    /// <param name="methods"></param>
    public static void Regenerate(IEnumerable<string> methods) {
      foreach (string s in methods)
        Regenerate(s);
    }

    public static void Regenerate(String name) {
      SdfInfo info = GetInfo(name);
      if (info != null)
        Regenerate(info);
    }

    public static void RegenerateAll() {
      foreach (SdfInfo info in GetAllInfos())
        Regenerate(info);
    }

    /// <summary>
    /// Generate code again for the given SDF with unchanged input and output cells, 
    /// recreate the corresponding Function, and update the delegate table entry
    /// </summary>
    /// <param name="info"></param>
    // TODO: This should *not* be applied to SDF's resulting from partial evaluation.
    public static void Regenerate(SdfInfo info) {
      cellToFunctionMapper.RemoveFunction(info);
      // Removing the SDF from Function enables CreateFunction to overwrite it
      Function.Remove(info.name);
      // Rebuild and add it back
      CreateFunction(info.name, info.outputCell, info.inputCells);
    }

    public static void DeleteFunction(string methodName) {
      SdfInfo info = GetInfo(methodName);
      if (info != null) {
        Unregister(info);
        cellToFunctionMapper.RemoveFunction(info);
        Function.Remove(methodName);
      }
    }

    internal static string[] CheckForModifications(List<FullCellAddr> changedCells) {
      return cellToFunctionMapper.GetFunctionsUsingAddresses(changedCells).ToArray();
    }
  }

  /// <summary>
  /// An SdfInfo instance represents a compiled sheet-defined function (SDF).
  /// </summary>
  public class SdfInfo {
    public readonly FullCellAddr outputCell;
    public readonly FullCellAddr[] inputCells;
    public readonly string name;           // Always upper case
    public readonly int index;             // Index into SdfManager.sdfDelegates
    public readonly int arity;
    public ProgramLines Program { get; set; }
    public bool IsVolatile { get; private set; }

    private static readonly Type[] sdfDelegateType
      = { typeof(Func<Value>),
          typeof(Func<Value,Value>),
          typeof(Func<Value,Value,Value>),
          typeof(Func<Value,Value,Value,Value>),
          typeof(Func<Value,Value,Value,Value,Value>),
          typeof(Func<Value,Value,Value,Value,Value,Value>),
          typeof(Func<Value,Value,Value,Value,Value,Value,Value>),
          typeof(Func<Value,Value,Value,Value,Value,Value,Value,Value>),
          typeof(Func<Value,Value,Value,Value,Value,Value,Value,Value,Value>),
          typeof(Func<Value,Value,Value,Value,Value,Value,Value,Value,Value,Value>),
          typeof(Func<Value,Value,Value,Value,Value,Value,Value,Value,Value,Value,Value>) };
    private static readonly MethodInfo[] sdfDelegateInvokeMethods;
    private static readonly Type[][] argumentTypes;

    public static readonly FieldInfo indexField = typeof(SdfInfo).GetField("index");

    static SdfInfo() {
      sdfDelegateInvokeMethods = new MethodInfo[sdfDelegateType.Length];
      for (int i = 0; i < sdfDelegateType.Length; i++)
        sdfDelegateInvokeMethods[i] = sdfDelegateType[i].GetMethod("Invoke");
      argumentTypes = new Type[sdfDelegateType.Length][];
      for (int i = 0; i < argumentTypes.Length; i++) {
        argumentTypes[i] = new Type[i];
        for (int j = 0; j < i; j++)
          argumentTypes[i][j] = Value.type;
      }
    }

    internal SdfInfo(FullCellAddr outputCell, FullCellAddr[] inputCells,
                     string name, int index) {
      this.outputCell = outputCell;
      this.inputCells = inputCells;
      this.name = name.ToUpper();
      this.index = index;
      this.arity = inputCells.Length;
    }

    public Type[] MyArgumentTypes {
      get { return argumentTypes[arity]; }
    }

    public Type MyType {
      get { return sdfDelegateType[arity]; }
    }

    public static Type SdfDelegateType(int n) {
      return sdfDelegateType[n];
    }

    public MethodInfo MyInvoke {
      get { return sdfDelegateInvokeMethods[arity]; }
    }

    /// <summary>
    /// Determine whether the sheet-defined function involves any volatile cells.  
    /// Does not track volatility of normal cells or normal cell areas referred to.
    /// </summary>
    /// <param name="fcas">The set of function-sheet cells making up the function.</param>
    public void SetVolatility(IEnumerable<FullCellAddr> fcas) {
      bool isVolatile = false;
      foreach (FullCellAddr fca in fcas) {
        Cell cell = fca.sheet[fca.ca];
        if (cell != null && cell.IsVolatile) {
          isVolatile = true;
          break;
        }
      }
      this.IsVolatile = isVolatile;
    }

    public Value Apply(Value[] aa) {
      switch (arity) {
        case 0: return Call0();
        case 1: return Call1(aa[0]);
        case 2: return Call2(aa[0], aa[1]);
        case 3: return Call3(aa[0], aa[1], aa[2]);
        case 4: return Call4(aa[0], aa[1], aa[2], aa[3]);
        case 5: return Call5(aa[0], aa[1], aa[2], aa[3], aa[4]);
        case 6: return Call6(aa[0], aa[1], aa[2], aa[3], aa[4], aa[5]);
        case 7: return Call7(aa[0], aa[1], aa[2], aa[3], aa[4], aa[5], aa[6]);
        case 8: return Call8(aa[0], aa[1], aa[2], aa[3], aa[4], aa[5], aa[6], aa[7]);
        case 9: return Call9(aa[0], aa[1], aa[2], aa[3], aa[4], aa[5], aa[6], aa[7], aa[8]);
        default: return ErrorValue.tooManyArgsError;
      }
    }

    // These don't seem to be used reflexively (for code generation)

    public Value Call0() {
      if (arity == 0)
        return ((Func<Value>)(SdfManager.sdfDelegates[index]))();
      else
        return ErrorValue.argCountError;
    }

    public Value Call1(Value v0) {
      if (arity == 1)
        return ((Func<Value, Value>)(SdfManager.sdfDelegates[index]))(v0);
      else
        return ErrorValue.argCountError;
    }

    public Value Call2(Value v0, Value v1) {
      if (arity == 2)
        return ((Func<Value, Value, Value>)(SdfManager.sdfDelegates[index]))(v0, v1);
      else
        return ErrorValue.argCountError;
    }

    public Value Call3(Value v0, Value v1, Value v2) {
      if (arity == 3)
        return ((Func<Value, Value, Value, Value>)(SdfManager.sdfDelegates[index]))(v0, v1, v2);
      else
        return ErrorValue.argCountError;
    }

    public Value Call4(Value v0, Value v1, Value v2, Value v3) {
      if (arity == 4)
        return ((Func<Value, Value, Value, Value, Value>)(SdfManager.sdfDelegates[index]))(v0, v1, v2, v3);
      else
        return ErrorValue.argCountError;
    }

    public Value Call5(Value v0, Value v1, Value v2, Value v3, Value v4) {
      if (arity == 5)
        return ((Func<Value, Value, Value, Value, Value, Value>)(SdfManager.sdfDelegates[index]))(v0, v1, v2, v3, v4);
      else
        return ErrorValue.argCountError;
    }

    public Value Call6(Value v0, Value v1, Value v2, Value v3, Value v4, Value v5) {
      if (arity == 6)
        return ((Func<Value, Value, Value, Value, Value, Value, Value>)(SdfManager.sdfDelegates[index]))(v0, v1, v2, v3, v4, v5);
      else
        return ErrorValue.argCountError;
    }

    public Value Call7(Value v0, Value v1, Value v2, Value v3, Value v4, Value v5, Value v6) {
      if (arity == 7)
        return ((Func<Value, Value, Value, Value, Value, Value, Value, Value>)(SdfManager.sdfDelegates[index]))(v0, v1, v2, v3, v4, v5, v6);
      else
        return ErrorValue.argCountError;
    }

    public Value Call8(Value v0, Value v1, Value v2, Value v3, Value v4, Value v5, Value v6, Value v7) {
      if (arity == 8)
        return ((Func<Value, Value, Value, Value, Value, Value, Value, Value, Value>)(SdfManager.sdfDelegates[index]))(v0, v1, v2, v3, v4, v5, v6, v7);
      else
        return ErrorValue.argCountError;
    }

    public Value Call9(Value v0, Value v1, Value v2, Value v3, Value v4, Value v5, Value v6, Value v7, Value v8) {
      if (arity == 9)
        return ((Func<Value, Value, Value, Value, Value, Value, Value, Value, Value, Value>)(SdfManager.sdfDelegates[index]))(v0, v1, v2, v3, v4, v5, v6, v7, v8);
      else
        return ErrorValue.argCountError;
    }

    public Value Apply(Sheet sheet, Expr[] es, int col, int row) {
      // Arity is checked in the SdfInfo.CallN methods
      switch (es.Length) {
        case 0:
          return Call0();
        case 1:
          return Call1(es[0].Eval(sheet, col, row));
        case 2:
          return Call2(es[0].Eval(sheet, col, row), es[1].Eval(sheet, col, row));
        case 3:
          return Call3(es[0].Eval(sheet, col, row), es[1].Eval(sheet, col, row),
                       es[2].Eval(sheet, col, row));
        case 4:
          return Call4(es[0].Eval(sheet, col, row), es[1].Eval(sheet, col, row),
                       es[2].Eval(sheet, col, row), es[3].Eval(sheet, col, row));
        case 5:
          return Call5(es[0].Eval(sheet, col, row), es[1].Eval(sheet, col, row),
                       es[2].Eval(sheet, col, row), es[3].Eval(sheet, col, row),
                       es[4].Eval(sheet, col, row));
        case 6:
          return Call6(es[0].Eval(sheet, col, row), es[1].Eval(sheet, col, row),
                       es[2].Eval(sheet, col, row), es[3].Eval(sheet, col, row),
                       es[4].Eval(sheet, col, row), es[5].Eval(sheet, col, row));
        case 7:
          return Call7(es[0].Eval(sheet, col, row), es[1].Eval(sheet, col, row),
                       es[2].Eval(sheet, col, row), es[3].Eval(sheet, col, row),
                       es[4].Eval(sheet, col, row), es[5].Eval(sheet, col, row),
                       es[6].Eval(sheet, col, row));
        case 8:
          return Call8(es[0].Eval(sheet, col, row), es[1].Eval(sheet, col, row),
                       es[2].Eval(sheet, col, row), es[3].Eval(sheet, col, row),
                       es[4].Eval(sheet, col, row), es[5].Eval(sheet, col, row),
                       es[6].Eval(sheet, col, row), es[7].Eval(sheet, col, row));
        case 9:
          return Call9(es[0].Eval(sheet, col, row), es[1].Eval(sheet, col, row),
                       es[2].Eval(sheet, col, row), es[3].Eval(sheet, col, row),
                       es[4].Eval(sheet, col, row), es[5].Eval(sheet, col, row),
                       es[6].Eval(sheet, col, row), es[7].Eval(sheet, col, row),
                       es[8].Eval(sheet, col, row));
        default:
          return ErrorValue.Make("#FUNERR: Too many arguments");
      }
    }

    public override string ToString() {
      return String.Format("FUN {0} AT #{1}", name, index);
    }
  }
}