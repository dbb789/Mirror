using System.Collections.Generic;
using System.Linq;
using Mono.CecilX;
using Mono.CecilX.Cil;
using Mono.CecilX.Rocks;

namespace Mirror.Weaver
{
    // Processes [SyncVar] attribute fields in NetworkBehaviour
    // not static, because ILPostProcessor is multithreaded
    public class SyncVarAttributeProcessor
    {
        // ulong = 64 bytes
        const int SyncVarLimit = 64;

        AssemblyDefinition assembly;
        WeaverTypes weaverTypes;
        SyncVarAccessLists syncVarAccessLists;
        Logger Log;

        string HookParameterMessage(string hookName, TypeReference ValueType) =>
            $"void {hookName}({ValueType} oldValue, {ValueType} newValue)";

        public SyncVarAttributeProcessor(AssemblyDefinition assembly, WeaverTypes weaverTypes, SyncVarAccessLists syncVarAccessLists, Logger Log)
        {
            this.assembly = assembly;
            this.weaverTypes = weaverTypes;
            this.syncVarAccessLists = syncVarAccessLists;
            this.Log = Log;
        }

        // Get hook method if any
        public MethodDefinition GetHookMethod(TypeDefinition td, FieldDefinition syncVar, ref bool WeavingFailed)
        {
            CustomAttribute syncVarAttr = syncVar.GetCustomAttribute<SyncVarAttribute>();

            if (syncVarAttr == null)
                return null;

            string hookFunctionName = syncVarAttr.GetField<string>("hook", null);

            if (hookFunctionName == null)
                return null;

            return FindHookMethod(td, syncVar, hookFunctionName, ref WeavingFailed);
        }

        MethodDefinition FindHookMethod(TypeDefinition td, FieldDefinition syncVar, string hookFunctionName, ref bool WeavingFailed)
        {
            List<MethodDefinition> methods = td.GetMethods(hookFunctionName);

            List<MethodDefinition> methodsWith2Param = new List<MethodDefinition>(methods.Where(m => m.Parameters.Count == 2));

            if (methodsWith2Param.Count == 0)
            {
                Log.Error($"Could not find hook for '{syncVar.Name}', hook name '{hookFunctionName}'. " +
                    $"Method signature should be {HookParameterMessage(hookFunctionName, syncVar.FieldType)}",
                    syncVar);
                WeavingFailed = true;

                return null;
            }

            foreach (MethodDefinition method in methodsWith2Param)
            {
                if (MatchesParameters(syncVar, method))
                {
                    return method;
                }
            }

            Log.Error($"Wrong type for Parameter in hook for '{syncVar.Name}', hook name '{hookFunctionName}'. " +
                     $"Method signature should be {HookParameterMessage(hookFunctionName, syncVar.FieldType)}",
                   syncVar);
            WeavingFailed = true;

            return null;
        }

        bool MatchesParameters(FieldDefinition syncVar, MethodDefinition method)
        {
            // matches void onValueChange(T oldValue, T newValue)
            return method.Parameters[0].ParameterType.FullName == syncVar.FieldType.FullName &&
                   method.Parameters[1].ParameterType.FullName == syncVar.FieldType.FullName;
        }

        public MethodDefinition GenerateSyncVarGetter(FieldDefinition fd, string originalName, FieldDefinition netFieldId)
        {
            //Create the get method
            MethodDefinition get = new MethodDefinition(
                $"get_Network{originalName}", MethodAttributes.Public |
                                              MethodAttributes.SpecialName |
                                              MethodAttributes.HideBySig,
                    fd.FieldType);

            ILProcessor worker = get.Body.GetILProcessor();

            // [SyncVar] GameObject?
            if (fd.FieldType.Is<UnityEngine.GameObject>())
            {
                // return this.GetSyncVarGameObject(ref field, uint netId);
                // this.
                worker.Emit(OpCodes.Ldarg_0);
                worker.Emit(OpCodes.Ldarg_0);
                worker.Emit(OpCodes.Ldfld, netFieldId);
                worker.Emit(OpCodes.Ldarg_0);
                worker.Emit(OpCodes.Ldflda, fd);
                worker.Emit(OpCodes.Call, weaverTypes.getSyncVarGameObjectReference);
                worker.Emit(OpCodes.Ret);
            }
            // [SyncVar] NetworkIdentity?
            else if (fd.FieldType.Is<NetworkIdentity>())
            {
                // return this.GetSyncVarNetworkIdentity(ref field, uint netId);
                // this.
                worker.Emit(OpCodes.Ldarg_0);
                worker.Emit(OpCodes.Ldarg_0);
                worker.Emit(OpCodes.Ldfld, netFieldId);
                worker.Emit(OpCodes.Ldarg_0);
                worker.Emit(OpCodes.Ldflda, fd);
                worker.Emit(OpCodes.Call, weaverTypes.getSyncVarNetworkIdentityReference);
                worker.Emit(OpCodes.Ret);
            }
            else if (fd.FieldType.IsDerivedFrom<NetworkBehaviour>())
            {
                // return this.GetSyncVarNetworkBehaviour<T>(ref field, uint netId);
                // this.
                worker.Emit(OpCodes.Ldarg_0);
                worker.Emit(OpCodes.Ldarg_0);
                worker.Emit(OpCodes.Ldfld, netFieldId);
                worker.Emit(OpCodes.Ldarg_0);
                worker.Emit(OpCodes.Ldflda, fd);
                MethodReference getFunc = weaverTypes.getSyncVarNetworkBehaviourReference.MakeGeneric(assembly.MainModule, fd.FieldType);
                worker.Emit(OpCodes.Call, getFunc);
                worker.Emit(OpCodes.Ret);
            }
            // [SyncVar] int, string, etc.
            else
            {
                worker.Emit(OpCodes.Ldarg_0);
                worker.Emit(OpCodes.Ldfld, fd);
                worker.Emit(OpCodes.Ret);
            }

            get.Body.Variables.Add(new VariableDefinition(fd.FieldType));
            get.Body.InitLocals = true;
            get.SemanticsAttributes = MethodSemanticsAttributes.Getter;

            return get;
        }

        public MethodDefinition GenerateSyncVarSetter(TypeDefinition td, FieldDefinition fd, string originalName, long dirtyBit, FieldDefinition netFieldId, ref bool WeavingFailed)
        {
            //Create the set method
            MethodDefinition set = new MethodDefinition($"set_Network{originalName}", MethodAttributes.Public |
                                                                                      MethodAttributes.SpecialName |
                                                                                      MethodAttributes.HideBySig,
                    weaverTypes.Import(typeof(void)));

            ILProcessor worker = set.Body.GetILProcessor();

            // if (!SyncVarEqual(value, ref playerData))
            Instruction endOfMethod = worker.Create(OpCodes.Nop);

            // NOTE: SyncVar...Equal functions are static.
            // don't Emit Ldarg_0 aka 'this'.

            // new value to set
            worker.Emit(OpCodes.Ldarg_1);

            // reference to field to set
            // make generic version of SetSyncVar with field type
            if (fd.FieldType.Is<UnityEngine.GameObject>())
            {
                // reference to netId Field to set
                worker.Emit(OpCodes.Ldarg_0);
                worker.Emit(OpCodes.Ldfld, netFieldId);

                worker.Emit(OpCodes.Call, weaverTypes.syncVarGameObjectEqualReference);
            }
            else if (fd.FieldType.Is<NetworkIdentity>())
            {
                // reference to netId Field to set
                worker.Emit(OpCodes.Ldarg_0);
                worker.Emit(OpCodes.Ldfld, netFieldId);

                worker.Emit(OpCodes.Call, weaverTypes.syncVarNetworkIdentityEqualReference);
            }
            else if (fd.FieldType.IsDerivedFrom<NetworkBehaviour>())
            {
                // reference to netId Field to set
                worker.Emit(OpCodes.Ldarg_0);
                worker.Emit(OpCodes.Ldfld, netFieldId);

                MethodReference getFunc = weaverTypes.syncVarNetworkBehaviourEqualReference.MakeGeneric(assembly.MainModule, fd.FieldType);
                worker.Emit(OpCodes.Call, getFunc);
            }
            else
            {
                worker.Emit(OpCodes.Ldarg_0);
                worker.Emit(OpCodes.Ldflda, fd);

                GenericInstanceMethod syncVarEqualGm = new GenericInstanceMethod(weaverTypes.syncVarEqualReference);
                syncVarEqualGm.GenericArguments.Add(fd.FieldType);
                worker.Emit(OpCodes.Call, syncVarEqualGm);
            }

            worker.Emit(OpCodes.Brtrue, endOfMethod);

            // T oldValue = value;
            // TODO for GO/NI we need to backup the netId don't we?
            VariableDefinition oldValue = new VariableDefinition(fd.FieldType);
            set.Body.Variables.Add(oldValue);
            worker.Emit(OpCodes.Ldarg_0);
            worker.Emit(OpCodes.Ldfld, fd);
            worker.Emit(OpCodes.Stloc, oldValue);

            // make generic instance of SyncVar<T> type for the type of 'value'
            TypeReference syncVarT_ForValue = weaverTypes.SyncVarT_Type.MakeGenericInstanceType(fd.FieldType);

            // SyncVar<T> test = new SyncVar<T>(value);
            Log.Warning("[SyncVar] " + fd.Name + " type=" + fd.FieldType + " SyncVar<type> = " + syncVarT_ForValue);
            VariableDefinition testSyncVar_T = new VariableDefinition(syncVarT_ForValue);
            set.Body.Variables.Add(testSyncVar_T);
            worker.Emit(OpCodes.Ldarg_0);   // 'this'
            worker.Emit(OpCodes.Ldfld, fd); // value = fd
            worker.Emit(OpCodes.Ldnull);    // hook = null
            // make generic ctor for SyncVar<T> for the target type SyncVar<T> with type of 'value'
            GenericInstanceType syncVarT_GenericInstanceType = (GenericInstanceType)syncVarT_ForValue;
            MethodReference syncVarT_Ctor_ForValue = weaverTypes.SyncVarT_GenericConstructor.MakeHostInstanceGeneric(assembly.MainModule, syncVarT_GenericInstanceType);
            worker.Emit(OpCodes.Newobj, syncVarT_Ctor_ForValue);

            // store result in our test variable
            worker.Emit(OpCodes.Stloc, testSyncVar_T);

            // this
            worker.Emit(OpCodes.Ldarg_0);

            // new value to set
            worker.Emit(OpCodes.Ldarg_1);

            // reference to field to set
            worker.Emit(OpCodes.Ldarg_0);
            worker.Emit(OpCodes.Ldflda, fd);

            // dirty bit
            // 8 byte integer aka long
            worker.Emit(OpCodes.Ldc_I8, dirtyBit);

            if (fd.FieldType.Is<UnityEngine.GameObject>())
            {
                // reference to netId Field to set
                worker.Emit(OpCodes.Ldarg_0);
                worker.Emit(OpCodes.Ldflda, netFieldId);

                worker.Emit(OpCodes.Call, weaverTypes.setSyncVarGameObjectReference);
            }
            else if (fd.FieldType.Is<NetworkIdentity>())
            {
                // reference to netId Field to set
                worker.Emit(OpCodes.Ldarg_0);
                worker.Emit(OpCodes.Ldflda, netFieldId);

                worker.Emit(OpCodes.Call, weaverTypes.setSyncVarNetworkIdentityReference);
            }
            else if (fd.FieldType.IsDerivedFrom<NetworkBehaviour>())
            {
                // reference to netId Field to set
                worker.Emit(OpCodes.Ldarg_0);
                worker.Emit(OpCodes.Ldflda, netFieldId);

                MethodReference getFunc = weaverTypes.setSyncVarNetworkBehaviourReference.MakeGeneric(assembly.MainModule, fd.FieldType);
                worker.Emit(OpCodes.Call, getFunc);
            }
            else
            {
                // make generic version of SetSyncVar with field type
                GenericInstanceMethod gm = new GenericInstanceMethod(weaverTypes.setSyncVarReference);
                gm.GenericArguments.Add(fd.FieldType);

                // invoke SetSyncVar
                worker.Emit(OpCodes.Call, gm);
            }

            MethodDefinition hookMethod = GetHookMethod(td, fd, ref WeavingFailed);

            if (hookMethod != null)
            {
                //if (NetworkServer.localClientActive && !getSyncVarHookGuard(dirtyBit))
                Instruction label = worker.Create(OpCodes.Nop);
                worker.Emit(OpCodes.Call, weaverTypes.NetworkServerGetLocalClientActive);
                worker.Emit(OpCodes.Brfalse, label);
                worker.Emit(OpCodes.Ldarg_0);
                worker.Emit(OpCodes.Ldc_I8, dirtyBit);
                worker.Emit(OpCodes.Call, weaverTypes.getSyncVarHookGuard);
                worker.Emit(OpCodes.Brtrue, label);

                // setSyncVarHookGuard(dirtyBit, true);
                worker.Emit(OpCodes.Ldarg_0);
                worker.Emit(OpCodes.Ldc_I8, dirtyBit);
                worker.Emit(OpCodes.Ldc_I4_1);
                worker.Emit(OpCodes.Call, weaverTypes.setSyncVarHookGuard);

                // call hook (oldValue, newValue)
                // Generates: OnValueChanged(oldValue, value);
                WriteCallHookMethodUsingArgument(worker, hookMethod, oldValue);

                // setSyncVarHookGuard(dirtyBit, false);
                worker.Emit(OpCodes.Ldarg_0);
                worker.Emit(OpCodes.Ldc_I8, dirtyBit);
                worker.Emit(OpCodes.Ldc_I4_0);
                worker.Emit(OpCodes.Call, weaverTypes.setSyncVarHookGuard);

                worker.Append(label);
            }

            worker.Append(endOfMethod);

            worker.Emit(OpCodes.Ret);

            set.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.In, fd.FieldType));
            set.SemanticsAttributes = MethodSemanticsAttributes.Setter;

            return set;
        }

        // ProcessSyncVar is called while iterating td.Fields.
        // can't add to it while iterating.
        // new fields are added to 'addedFields'
        public void ProcessSyncVar(TypeDefinition td, FieldDefinition fd, Dictionary<FieldDefinition, FieldDefinition> syncVarNetIds, long dirtyBit, List<FieldDefinition> addedFields, ref bool WeavingFailed)
        {
            string originalName = fd.Name;

            // GameObject/NetworkIdentity SyncVars have a new field for netId
            FieldDefinition netIdField = null;
            // NetworkBehaviour has different field type than other NetworkIdentityFields
            if (fd.FieldType.IsDerivedFrom<NetworkBehaviour>())
            {
                netIdField = new FieldDefinition($"___{fd.Name}NetId",
                   FieldAttributes.Private,
                   weaverTypes.Import<NetworkBehaviour.NetworkBehaviourSyncVar>());

                syncVarNetIds[fd] = netIdField;
            }
            else if (fd.FieldType.IsNetworkIdentityField())
            {
                netIdField = new FieldDefinition($"___{fd.Name}NetId",
                    FieldAttributes.Private,
                    weaverTypes.Import<uint>());

                syncVarNetIds[fd] = netIdField;
            }


            // make generic instance of SyncVar<T> type for the type of 'value'
            TypeReference syncVarT_ForValue = weaverTypes.SyncVarT_Type.MakeGenericInstanceType(fd.FieldType);
            FieldDefinition syncVarTField = new FieldDefinition($"___{fd.Name}SyncVarT", FieldAttributes.Public, syncVarT_ForValue);
            // TODO ctor
            addedFields.Add(syncVarTField);

            MethodDefinition get = GenerateSyncVarGetter(fd, originalName, netIdField);
            MethodDefinition set = GenerateSyncVarSetter(td, fd, originalName, dirtyBit, netIdField, ref WeavingFailed);

            //NOTE: is property even needed? Could just use a setter function?
            //create the property
            PropertyDefinition propertyDefinition = new PropertyDefinition($"Network{originalName}", PropertyAttributes.None, fd.FieldType)
            {
                GetMethod = get,
                SetMethod = set
            };

            //add the methods and property to the type.
            td.Methods.Add(get);
            td.Methods.Add(set);
            td.Properties.Add(propertyDefinition);
            syncVarAccessLists.replacementSetterProperties[fd] = set;

            // replace getter field if GameObject/NetworkIdentity so it uses
            // netId instead
            // -> only for GameObjects, otherwise an int syncvar's getter would
            //    end up in recursion.
            if (fd.FieldType.IsNetworkIdentityField())
            {
                syncVarAccessLists.replacementGetterProperties[fd] = get;
            }
        }

        public (List<FieldDefinition> syncVars, Dictionary<FieldDefinition, FieldDefinition> syncVarNetIds) ProcessSyncVars(TypeDefinition td, ref bool WeavingFailed)
        {
            List<FieldDefinition> syncVars = new List<FieldDefinition>();
            Dictionary<FieldDefinition, FieldDefinition> syncVarNetIds = new Dictionary<FieldDefinition, FieldDefinition>();

            // the mapping of dirtybits to sync-vars is implicit in the order of the fields here. this order is recorded in m_replacementProperties.
            // start assigning syncvars at the place the base class stopped, if any
            int dirtyBitCounter = syncVarAccessLists.GetSyncVarStart(td.BaseType.FullName);

            // separate list for added fields.
            // can't add while we iterate 'td.Fields' otherwise.
            List<FieldDefinition> addedFields = new List<FieldDefinition>();

            // find syncvars
            foreach (FieldDefinition fd in td.Fields)
            {
                if (fd.HasCustomAttribute<SyncVarAttribute>())
                {
                    if ((fd.Attributes & FieldAttributes.Static) != 0)
                    {
                        Log.Error($"{fd.Name} cannot be static", fd);
                        WeavingFailed = true;
                        continue;
                    }

                    if (fd.FieldType.IsArray)
                    {
                        Log.Error($"{fd.Name} has invalid type. Use SyncLists instead of arrays", fd);
                        WeavingFailed = true;
                        continue;
                    }

                    if (SyncObjectInitializer.ImplementsSyncObject(fd.FieldType))
                    {
                        Log.Warning($"{fd.Name} has [SyncVar] attribute. SyncLists should not be marked with SyncVar", fd);
                    }
                    else
                    {
                        syncVars.Add(fd);

                        ProcessSyncVar(td, fd, syncVarNetIds, 1L << dirtyBitCounter, addedFields, ref WeavingFailed);
                        dirtyBitCounter += 1;

                        if (dirtyBitCounter > SyncVarLimit)
                        {
                            Log.Error($"{td.Name} has > {SyncVarLimit} SyncVars. Consider refactoring your class into multiple components", td);
                            WeavingFailed = true;
                            continue;
                        }
                    }
                }
            }

            // add all added fields
            foreach (FieldDefinition fd in addedFields)
            {
                td.Fields.Add(fd);
            }

            // add all the new SyncVar __netId fields
            foreach (FieldDefinition fd in syncVarNetIds.Values)
            {
                td.Fields.Add(fd);
            }
            syncVarAccessLists.SetNumSyncVars(td.FullName, syncVars.Count);

            return (syncVars, syncVarNetIds);
        }

        public void WriteCallHookMethodUsingArgument(ILProcessor worker, MethodDefinition hookMethod, VariableDefinition oldValue)
        {
            WriteCallHookMethod(worker, hookMethod, oldValue, null);
        }

        public void WriteCallHookMethodUsingField(ILProcessor worker, MethodDefinition hookMethod, VariableDefinition oldValue, FieldDefinition newValue, ref bool WeavingFailed)
        {
            if (newValue == null)
            {
                Log.Error("NewValue field was null when writing SyncVar hook");
                WeavingFailed = true;
            }

            WriteCallHookMethod(worker, hookMethod, oldValue, newValue);
        }

        void WriteCallHookMethod(ILProcessor worker, MethodDefinition hookMethod, VariableDefinition oldValue, FieldDefinition newValue)
        {
            WriteStartFunctionCall();

            // write args
            WriteOldValue();
            WriteNewValue();

            WriteEndFunctionCall();


            // *** Local functions used to write OpCodes ***
            // Local functions have access to function variables, no need to pass in args

            void WriteOldValue()
            {
                worker.Emit(OpCodes.Ldloc, oldValue);
            }

            void WriteNewValue()
            {
                // write arg1 or this.field
                if (newValue == null)
                {
                    worker.Emit(OpCodes.Ldarg_1);
                }
                else
                {
                    // this.
                    worker.Emit(OpCodes.Ldarg_0);
                    // syncvar.get
                    worker.Emit(OpCodes.Ldfld, newValue);
                }
            }

            // Writes this before method if it is not static
            void WriteStartFunctionCall()
            {
                // don't add this (Ldarg_0) if method is static
                if (!hookMethod.IsStatic)
                {
                    // this before method call
                    // e.g. this.onValueChanged
                    worker.Emit(OpCodes.Ldarg_0);
                }
            }

            // Calls method
            void WriteEndFunctionCall()
            {
                // only use Callvirt when not static
                OpCode opcode = hookMethod.IsStatic ? OpCodes.Call : OpCodes.Callvirt;
                worker.Emit(opcode, hookMethod);
            }
        }
    }
}
