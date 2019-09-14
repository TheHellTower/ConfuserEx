﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Confuser.Core;
using Confuser.Renamer.References;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.MD;

namespace Confuser.Renamer.Analyzers {
	public sealed class TypeBlobAnalyzer : IRenamer {
		void IRenamer.Analyze(ConfuserContext context, INameService service, ProtectionParameters parameters, IDnlibDef def) {
			if (!(def is ModuleDefMD moduleDef)) return;

			Analyze(service, context.Modules, context.Logger, moduleDef);
		}

		public static void Analyze(INameService service, ICollection<ModuleDefMD> modules, Core.ILogger logger, ModuleDefMD module) {
			// MemberRef
			var table = module.TablesStream.Get(Table.Method);
			var len = table.Rows;
			IEnumerable<MethodDef> methods = module.GetTypes().SelectMany(type => type.Methods);
			foreach (MethodDef method in methods) {
				foreach (MethodOverride methodImpl in method.Overrides) {
					if (methodImpl.MethodBody is MemberRef)
						AnalyzeMemberRef(modules, service, (MemberRef)methodImpl.MethodBody);
					if (methodImpl.MethodDeclaration is MemberRef)
						AnalyzeMemberRef(modules, service, (MemberRef)methodImpl.MethodDeclaration);
				}
				if (!method.HasBody)
					continue;
				foreach (Instruction instr in method.Body.Instructions) {
					if (instr.Operand is MemberRef)
						AnalyzeMemberRef(modules, service, (MemberRef)instr.Operand);
					else if (instr.Operand is MethodSpec) {
						var spec = (MethodSpec)instr.Operand;
						if (spec.Method is MemberRef)
							AnalyzeMemberRef(modules, service, (MemberRef)spec.Method);
					}
				}
			}


			// CustomAttribute
			table = module.TablesStream.Get(Table.CustomAttribute);
			len = table.Rows;
			var attrs = Enumerable.Range(1, (int)len)
				.Select(rid => {
					if (module.TablesStream.TryReadCustomAttributeRow((uint)rid, out var row)) {
						return module.ResolveHasCustomAttribute(row.Parent);
					}
					return null;
				})
				.Where(a => a != null)
				.Distinct()
				.SelectMany(owner => owner.CustomAttributes);
			foreach (CustomAttribute attr in attrs) {
				if (attr.Constructor is MemberRef)
					AnalyzeMemberRef(modules, service, (MemberRef)attr.Constructor);

				foreach (CAArgument arg in attr.ConstructorArguments)
					AnalyzeCAArgument(modules, service, arg);

				foreach (CANamedArgument arg in attr.Fields)
					AnalyzeCAArgument(modules, service, arg.Argument);

				foreach (CANamedArgument arg in attr.Properties)
					AnalyzeCAArgument(modules, service, arg.Argument);

				TypeDef attrType = attr.AttributeType.ResolveTypeDefThrow();
				if (!modules.Contains((ModuleDefMD)attrType.Module))
					continue;

				foreach (CANamedArgument fieldArg in attr.Fields) {
					FieldDef field = attrType.FindField(fieldArg.Name, new FieldSig(fieldArg.Type));
					if (field == null)
						logger.WarnFormat("Failed to resolve CA field '{0}::{1} : {2}'.", attrType, fieldArg.Name, fieldArg.Type);
					else
						service.AddReference(field, new CAMemberReference(fieldArg, field));
				}
				foreach (CANamedArgument propertyArg in attr.Properties) {
					PropertyDef property = attrType.FindProperty(propertyArg.Name, new PropertySig(true, propertyArg.Type));
					if (property == null)
						logger.WarnFormat("Failed to resolve CA property '{0}::{1} : {2}'.", attrType, propertyArg.Name, propertyArg.Type);
					else
						service.AddReference(property, new CAMemberReference(propertyArg, property));
				}
			}
		}

		public void PreRename(ConfuserContext context, INameService service, ProtectionParameters parameters, IDnlibDef def) {
			//
		}

		public void PostRename(ConfuserContext context, INameService service, ProtectionParameters parameters, IDnlibDef def) {
			//
		}

		private static void AnalyzeCAArgument(ICollection<ModuleDefMD> modules, INameService service, CAArgument arg) {
			if (arg.Value == null) return; // null was passed to the custom attribute. We'll ignore that.

			if (arg.Type.DefinitionAssembly.IsCorLib() && arg.Type.FullName == "System.Type") {
				var typeSig = (TypeSig)arg.Value;
				foreach (ITypeDefOrRef typeRef in typeSig.FindTypeRefs()) {
					TypeDef typeDef = typeRef.ResolveTypeDefThrow();
					if (modules.Contains((ModuleDefMD)typeDef.Module)) {
						if (typeRef is TypeRef)
							service.AddReference(typeDef, new TypeRefReference((TypeRef)typeRef, typeDef));
						service.ReduceRenameMode(typeDef, RenameMode.ASCII);
					}
				}
			}
			else if (arg.Value is CAArgument[]) {
				foreach (CAArgument elem in (CAArgument[])arg.Value)
					AnalyzeCAArgument(modules, service, elem);
			}
		}

		private static void AnalyzeMemberRef(ICollection<ModuleDefMD> modules, INameService service, MemberRef memberRef) {
			ITypeDefOrRef declType = memberRef.DeclaringType;
			var typeSpec = declType as TypeSpec;
			if (typeSpec == null || typeSpec.TypeSig.IsArray || typeSpec.TypeSig.IsSZArray)
				return;

			TypeSig sig = typeSpec.TypeSig;
			while (sig.Next != null)
				sig = sig.Next;


			Debug.Assert(sig is TypeDefOrRefSig || sig is GenericInstSig || sig is GenericSig);
			if (sig is GenericInstSig) {
				var inst = (GenericInstSig)sig;
				Debug.Assert(!(inst.GenericType.TypeDefOrRef is TypeSpec));
				TypeDef openType = inst.GenericType.TypeDefOrRef.ResolveTypeDefThrow();
				if (!modules.Contains((ModuleDefMD)openType.Module) ||
					memberRef.IsArrayAccessors())
					return;

				IDnlibDef member;
				if (memberRef.IsFieldRef) member = memberRef.ResolveFieldThrow();
				else if (memberRef.IsMethodRef) member = memberRef.ResolveMethodThrow();
				else throw new UnreachableException();

				service.AddReference(member, new MemberRefReference(memberRef, member));
			}
		}
	}
}
