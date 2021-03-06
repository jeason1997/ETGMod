﻿using System;
using System.Reflection;
using System.Collections.Generic;
using Eluant;

namespace ETGMod.Lua {
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public class ForbidLuaHookingAttribute : Attribute {}

    public class HookManager : IDisposable {
        public Dictionary<long, LuaFunction> Hooks = new Dictionary<long, LuaFunction>();
        public Dictionary<long, System.Type> HookReturns = new Dictionary<long, System.Type>();

        public static List<string> ForbiddenNamespaces = new List<string> {
            "ETGMod",
            "System",
            "TexMod"
        };

        private static Logger _Logger = new Logger("HookManager");

        public void Dispose() {
            foreach (var kv in Hooks) {
                kv.Value.Dispose();
            }
        }

        private MethodInfo _TryFindMethod(Type type, string name, Type[] argtypes, bool instance, bool @public) {
            BindingFlags binding_flags = 0;

            if (instance) binding_flags |= BindingFlags.Instance;
            else binding_flags |= BindingFlags.Static;

            if (@public) binding_flags |= BindingFlags.Public;
            else binding_flags |= BindingFlags.NonPublic;

            // not sure if this is needed
            if (argtypes == null) return type.GetMethod(name, binding_flags);
            else return type.GetMethod(name, binding_flags, null, argtypes, null);

        }


        private void _CheckForbidden(MethodInfo method) {
            var @namespace = method.DeclaringType.Namespace;
            for (int i = 0; i < ForbiddenNamespaces.Count; i++) {
                var forbidden_namespace = ForbiddenNamespaces[i];
                if (@namespace == forbidden_namespace) {
                    throw new LuaException($"Tried to hook a method in a type in namespace '{@namespace}', but hooking methods in this namespace is forbidden");
                }
                var nsbegin = ForbiddenNamespaces[i] + '.';
                if (@namespace.StartsWithInvariant(nsbegin)) {
                    throw new LuaException($"Tried to hook a method in a type in namespace '{@namespace}', but hooking methods in sub-namespaces of '{forbidden_namespace}' is forbidden.");
                }
            }

            var attrs = method.GetCustomAttributes(typeof(ForbidLuaHookingAttribute), true);
            if (attrs.Length > 0 && attrs[0] is ForbidLuaHookingAttribute) {
                throw new LuaException($"Hooking method '{method.Name}' in type '{method.DeclaringType.FullName}' is explicitly forbidden");
            }

            var impl = method.GetMethodImplementationFlags();

            if ((impl & MethodImplAttributes.InternalCall) != 0) {
                throw new LuaException($"Hooking method '{method.Name}' in type '{method.DeclaringType.FullName}' is forbidden by default as it is an extern method");
            }

            // all good if it gets here (hopefully!)
        }

        public void Add(LuaTable details, LuaFunction fn) {
            Type criteria_type;
            string criteria_methodname;
            Type[] criteria_argtypes = null;
            bool criteria_instance;
            bool criteria_public;
            bool hook_returns;

            using (var ftype = details["type"]) {
                if (ftype == null) {
                    throw new LuaException($"type: Expected Type, got null");
                }
                if (!(ftype is IClrObject)) {
                    throw new LuaException($"type: Expected CLR Type object, got non-CLR object of type {ftype.GetType()}");
                } else if (!(((IClrObject)ftype).ClrObject is Type)) {
                    throw new LuaException($"type: Expected CLR Type object, got CLR object of type {((IClrObject)ftype).ClrObject.GetType()}");
                }

                criteria_type = ((IClrObject)ftype).ClrObject as Type;
                using (var method = details["method"] as LuaString)
                using (var instance = details["instance"] as LuaBoolean)
                using (var @public = details["public"] as LuaBoolean)
                using (var returns = details["returns"] as LuaBoolean)
                using (var args = details["args"] as LuaTable) {
                    if (method == null) throw new LuaException("method: Expected string, got null");

                    if (args != null) {
                        var count = 0;
                        while (true) {
                            using (var value = args[count + 1]) {
                                if (value is LuaNil) break;
                                if (!(value is IClrObject)) {
                                    throw new LuaException($"args: Expected entry at index {count} to be a CLR Type object, got non-CLR object of type {value.GetType()}");
                                } else if (!(((IClrObject)value).ClrObject is Type)) {
                                    throw new LuaException($"args: Expected entry at index {count} to be a CLR Type object, got CLR object of type {((IClrObject)value).ClrObject.GetType()}");
                                }
                            }
                            count += 1;
                        }

                        var argtypes = new Type[args.Count];

                        for (int i = 1; i <= count; i++) {
                            using (var value = args[i]) {
                                argtypes[i - 1] = (Type)args[i].CLRMappedObject;
                            }
                        }

                        criteria_argtypes = argtypes;
                    }

                    criteria_instance = instance?.ToBoolean() ?? true;
                    criteria_public = @public?.ToBoolean() ?? true;
                    criteria_methodname = method.ToString();

                    hook_returns = returns?.ToBoolean() ?? false;
                }
            }

            var method_info = _TryFindMethod(
                criteria_type,
                criteria_methodname,
                criteria_argtypes,
                criteria_instance,
                criteria_public
            );

            if (method_info == null) {
                throw new LuaException($"Method '{criteria_methodname}' in '{criteria_type.FullName}' not found.");
            }
            _CheckForbidden(method_info);
            
            RuntimeHooks.InstallDispatchHandler(method_info);

            var token = RuntimeHooks.MethodToken(method_info);
            Hooks[token] = fn;
            fn.DisposeAfterManagedCall = false;

            if (hook_returns) {
                HookReturns[token] = method_info.ReturnType;
            }

            _Logger.Debug($"Added Lua hook for method '{criteria_methodname}' ({token})");
        }

        internal object TryRun(LuaRuntime runtime, long token, object target, object[] args, out bool returned) {
            _Logger.Debug($"Trying to run method hook (token {token})");
            returned = false;

            object return_value = null;

            LuaFunction fun;
            if (Hooks.TryGetValue(token, out fun)) {
                _Logger.Debug($"Hook found");
                // target == null --> static

                var objs_offs = 1;
                if (target == null) objs_offs = 0;

                var objs = new LuaValue[args.Length + objs_offs];
                if (target != null) objs[0] = runtime.AsLuaValue(target);
                for (int i = 0; i < args.Length; i++) {
                    objs[i + objs_offs] = runtime.AsLuaValue(args[i]);
                }

                var result = fun.Call(args: objs);

                Type return_type;
                if (HookReturns.TryGetValue(token, out return_type)) {
                    if (result.Count > 1) {
                        for (int i = 1; i < result.Count; i++) result[i].Dispose();
                    }

                    if (result.Count > 0) {
                        returned = true;
                        return_value = runtime.ToClrObject(result[0], return_type);

                        if (return_value != result[0]) result[0].Dispose();
                    }
                } else {
                    result.Dispose();
                }

                for (int i = 0; i < objs.Length; i++) objs[i]?.Dispose();
            } else _Logger.Debug($"Hook not found");

            return return_value;
        }
    }
}
