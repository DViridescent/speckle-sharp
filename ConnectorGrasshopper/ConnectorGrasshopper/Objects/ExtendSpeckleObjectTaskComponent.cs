﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConnectorGrasshopper.Extras;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Speckle.Core.Logging;
using Speckle.Core.Models;
using Utilities = ConnectorGrasshopper.Extras.Utilities;

namespace ConnectorGrasshopper.Objects
{
  public class ExtendSpeckleObjectTaskComponent : SelectKitTaskCapableComponentBase<Base>,
    IGH_VariableParameterComponent
  {
    protected override Bitmap Icon => Properties.Resources.ExtendSpeckleObject;
    public override GH_Exposure Exposure => GH_Exposure.tertiary;
    public override Guid ComponentGuid => new Guid("2D455B11-F372-47E5-98BE-515EA758A669");

    public ExtendSpeckleObjectTaskComponent() : base("Extend Speckle Object", "ESO",
      "Allows you to extend a Speckle object by setting its keys and values.",
      ComponentCategories.PRIMARY_RIBBON, ComponentCategories.OBJECTS)
    {
    }

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
      var pObj = pManager.AddParameter(new SpeckleBaseParam("Speckle Object", "O",
        "Speckle object to deconstruct into it's properties.", GH_ParamAccess.item));
      // All other inputs are dynamically generated by the user.
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
      pManager.AddParameter(new SpeckleBaseParam("Speckle Object", "O", "Created speckle object", GH_ParamAccess.item));
    }


    protected override void SolveInstance(IGH_DataAccess DA)
    {
      if (InPreSolve)
      {
        GH_SpeckleBase speckleBase = null;
        DA.GetData(0, ref speckleBase);
        var @base = speckleBase.Value.ShallowCopy();
        var inputData = new Dictionary<string, object>();
        if (Params.Input.Count == 1)
        {
          inputData = null;
          return;
        }

        var hasErrors = false;
        var allOptional = Params.Input.FindAll(p => p.Optional).Count == Params.Input.Count;
        if (Params.Input.Count > 1 && allOptional)
        {
          AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "You cannot set all parameters as optional");
          inputData = null;
          return;
        }

        inputData = new Dictionary<string, object>();
        for (int i = 1; i < Params.Input.Count; i++)
        {
          var ighParam = Params.Input[i];
          var param = ighParam as GenericAccessParam;
          var index = Params.IndexOfInputParam(param.Name);
          var detachable = param.Detachable;
          var key = detachable ? "@" + param.NickName : param.NickName;

          var willOverwrite = @base.GetMembers().ContainsKey(key);
          var targetIndex = DA.ParameterTargetIndex(0);
          var path = DA.ParameterTargetPath(0);
          if (willOverwrite)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
              $"Key {key} already exists in object at {path}[{targetIndex}], its value will be overwritten");
          switch (param.Access)
          {
            case GH_ParamAccess.item:
              object value = null;
              DA.GetData(index, ref value);
              if (!param.Optional && value == null)
              {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                  $"Non-optional parameter {param.NickName} cannot be null");
                hasErrors = true;
              }

              inputData[key] = value;
              break;
            case GH_ParamAccess.list:
              var values = new List<object>();
              DA.GetDataList(index, values);
              if (!param.Optional)
              {
                if (values.Count == 0)
                {
                  AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"Non-optional parameter {param.NickName} cannot be null or empty.");
                  hasErrors = true;
                }
              }

              if (values.Any(p => p == null))
              {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                  $"List access parameter {param.NickName} cannot contain null values. Please clean your data tree.");
                hasErrors = true;
              }

              inputData[key] = values;
              break;
            case GH_ParamAccess.tree:
              break;
            default:
              throw new ArgumentOutOfRangeException();
          }
        }

        if (hasErrors) inputData = null;

        var task = Task.Run(() => DoWork(@base, inputData));
        TaskList.Add(task);
        return;
      }

      // Report all conversion errors as warnings
      if (Converter != null)
        foreach (var error in Converter.ConversionErrors)
          AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, error.Message + ": " + error.InnerException?.Message);


      if (!GetSolveResults(DA, out Base result))
      {
        // Normal mode not supported
        return;
      }

      if (result != null)
      {
        DA.SetData(0, result);
      }
    }

    public bool CanInsertParameter(GH_ParameterSide side, int index) => side == GH_ParameterSide.Input && index != 0;

    public bool CanRemoveParameter(GH_ParameterSide side, int index) => side == GH_ParameterSide.Input && index != 0;

    public IGH_Param CreateParameter(GH_ParameterSide side, int index)
    {
      var myParam = new GenericAccessParam
      {
        Name = GH_ComponentParamServer.InventUniqueNickname("ABCD", Params.Input),
        MutableNickName = true,
        Optional = true
      };

      myParam.NickName = myParam.Name;
      myParam.Optional = false;
      myParam.ObjectChanged += (sender, e) => { };
      return myParam;
    }

    public bool DestroyParameter(GH_ParameterSide side, int index)
    {
      return true;
    }

    public void VariableParameterMaintenance()
    {
    }

    public Base DoWork(Base @base, Dictionary<string, object> inputData)
    {
      try
      {
        var hasErrors = false;

        inputData?.Keys.ToList().ForEach(key =>
        {
          var value = inputData[key];


          if (value is List<object> list)
          {
            // Value is a list of items, iterate and convert.
            List<object> converted = null;
            try
            {
              converted = list.Select(item =>
              {
                return Converter != null ? Utilities.TryConvertItemToSpeckle(item, Converter) : item;
              }).ToList();
            }
            catch (Exception e)
            {
              Log.CaptureException(e);
              AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"{e.Message}");
              hasErrors = true;
            }

            try
            {
              @base[key] = converted;
            }
            catch (Exception e)
            {
              Log.CaptureException(e);
              AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"{e.Message}");
              hasErrors = true;
            }
          }
          else
          {
            // If value is not list, it is a single item.

            try
            {
              if (Converter != null)
                @base[key] = value == null ? null : Utilities.TryConvertItemToSpeckle(value, Converter);
              else
                @base[key] = value;
            }
            catch (Exception e)
            {
              Log.CaptureException(e);
              AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"{e.Message}");
              hasErrors = true;
            }
          }
        });

        if (hasErrors)
        {
          @base = null;
        }
      }
      catch (Exception e)
      {
        // If we reach this, something happened that we weren't expecting...
        Log.CaptureException(e);
        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Something went terribly wrong... " + e.Message);
      }

      return @base;
    }
  }
}
