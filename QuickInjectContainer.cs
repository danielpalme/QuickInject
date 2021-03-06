﻿namespace QuickInject
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using BuildPlanVisitors;
    using Microsoft.Practices.ObjectBuilder2;
    using Microsoft.Practices.Unity;

    public class QuickInjectContainer : IQuickInjectContainer, IUnityContainer, IServiceProvider
    {
        private static readonly Type UnityContainerType = typeof(IUnityContainer);

        private static readonly ResolverOverride[] NoResolverOverrides = { };

        private static readonly QuickInjectEventSource Logger = new QuickInjectEventSource();

        private readonly object lockObj = new object();

        private static readonly EmptyBlockExpressionRemovalBuildPlanVisitor EmptyBlockExpressionRemovalVisitor = new EmptyBlockExpressionRemovalBuildPlanVisitor();

        private readonly QuickInjectContainer parentContainer;

        private readonly Dictionary<Type, Type> typeMappingTable = new Dictionary<Type, Type>();

        private readonly Dictionary<Type, LifetimeManager> lifetimeTable = new Dictionary<Type, LifetimeManager>();

        private readonly Dictionary<Type, Expression> factoryExpressionTable = new Dictionary<Type, Expression>();

        private readonly List<IBuildPlanVisitor> buildPlanVisitors = new List<IBuildPlanVisitor>();

        private readonly ExtensionImpl extensionImpl;

        private readonly List<QuickInjectContainer> children = new List<QuickInjectContainer>();

        private ImmutableDictionary<Type, PropertyInfo[]> propertyInfoTable = ImmutableDictionary<Type, PropertyInfo[]>.Empty;

        private ImmutableDictionary<Type, Func<object>> buildPlanTable = ImmutableDictionary<Type, Func<object>>.Empty;

        private Action<ITreeNode<Type>> dependencyTreeListener;

        public QuickInjectContainer()
        {
            this.extensionImpl = new ExtensionImpl(this, new DummyPolicyList());

            this.factoryExpressionTable.Add(UnityContainerType, Expression.Constant(this));

            this.Registering += delegate { };
            this.RegisteringInstance += delegate { };
            this.ChildContainerCreated += delegate { };
        }

        private QuickInjectContainer(QuickInjectContainer parent)
        {
            if (parent == null)
            {
                throw new ArgumentNullException("parent");
            }

            this.Registering += delegate { };
            this.RegisteringInstance += delegate { };
            this.ChildContainerCreated += delegate { };

            this.RegisterDependencyTreeListener(parent.dependencyTreeListener);
            foreach (var visitor in parent.buildPlanVisitors)
            {
                this.AddBuildPlanVisitor(visitor);
            }

            this.parentContainer = parent;
            this.extensionImpl = this.parentContainer.extensionImpl;

            this.factoryExpressionTable.Add(UnityContainerType, Expression.Constant(this));
        }

        internal event EventHandler<RegisterEventArgs> Registering;

        internal event EventHandler<RegisterInstanceEventArgs> RegisteringInstance;

        internal event EventHandler<ChildContainerCreatedEventArgs> ChildContainerCreated;

        public IUnityContainer Parent
        {
            get
            {
                return this.parentContainer;
            }
        }

        public IEnumerable<ContainerRegistration> Registrations
        {
            get
            {
                throw new NotSupportedException();
            }
        }

        public IUnityContainer AddExtension(UnityContainerExtension extension)
        {
            extension.InitializeExtension(this.extensionImpl);
            return this;
        }

        public object BuildUp(Type t, object existing, string name, params ResolverOverride[] resolverOverrides)
        {
            PropertyInfo[] propertyInfos = this.propertyInfoTable.GetValueOrDefault(t);
            if (propertyInfos != null)
            {
                foreach (PropertyInfo p in propertyInfos)
                {
                    p.SetValue(existing, this.Resolve(p.PropertyType, null, NoResolverOverrides));
                }
            }
            else
            {
                this.SlowBuildUp(existing, t);
            }

            return existing;
        }

        public object Configure(Type configurationInterface)
        {
            throw new NotSupportedException();
        }

        public IUnityContainer CreateChildContainer()
        {
            QuickInjectContainer child;
            ExtensionImpl childContext;

            lock (this.lockObj)
            {
                child = new QuickInjectContainer(this);
                childContext = new ExtensionImpl(child, new DummyPolicyList());
                this.children.Add(child);
            }

            // Must happen outside the lock to avoid deadlock between callers
            this.ChildContainerCreated(this, new ChildContainerCreatedEventArgs(childContext));

            return child;
        }

        public IUnityContainer RegisterInstance(Type t, string name, object instance, LifetimeManager lifetime)
        {
            if (instance == null)
            {
                throw new ArgumentNullException("instance");
            }

            if (lifetime == null)
            {
                throw new ArgumentNullException("lifetime");
            }

            if (!string.IsNullOrEmpty(name))
            {
                throw new NotSupportedException("Named registrations are not supported");
            }

            this.RegisteringInstance(this, new RegisterInstanceEventArgs(t, instance, name, lifetime));

            lock (this.lockObj)
            {
                lifetime.SetValue(instance);
                this.lifetimeTable.AddOrUpdate(t, lifetime);
                this.factoryExpressionTable.AddOrUpdate(t, Expression.Constant(instance));
                this.ClearBuildPlans();
            }

            return this;
        }

        public IUnityContainer RegisterType(Type from, Type to, string name, LifetimeManager lifetimeManager, params InjectionMember[] injectionMembers)
        {
            if (to == null)
            {
                throw new ArgumentNullException("to");
            }

            if (injectionMembers == null)
            {
                throw new ArgumentNullException("injectionMembers");
            }

            if ((from != null && from.GetTypeInfo().IsGenericTypeDefinition) || to.GetTypeInfo().IsGenericTypeDefinition)
            {
                throw new ArgumentException("Open Generic Types are not supported");
            }

            if (!string.IsNullOrEmpty(name))
            {
                throw new NotSupportedException("Named registrations are not supported");
            }

            if (injectionMembers.Length > 1)
            {
                throw new NotSupportedException("Multiple injection members are not supported");
            }

            Logger.RegisterType(from ?? to, to, lifetimeManager);
            this.Registering(this, new RegisterEventArgs(from, to, name, lifetimeManager));

            lock (this.lockObj)
            {
                if (from != null)
                {
                    this.typeMappingTable.AddOrUpdate(from, to);
                }

                if (lifetimeManager != null)
                {
                    this.lifetimeTable.AddOrUpdate(to, lifetimeManager);
                }

                if (injectionMembers.Length == 1)
                {
                    this.factoryExpressionTable.AddOrUpdate(to, injectionMembers[0].GenExpression(to, this));
                }

                this.ClearBuildPlans();
            }

            return this;
        }

        public IUnityContainer RemoveAllExtensions()
        {
            throw new NotSupportedException();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public object Resolve(Type t, string name, params ResolverOverride[] resolverOverrides)
        {
#if DEBUG
            if (Logger.IsEnabled())
            {
                Logger.Resolve(t.ToString());
            }
#endif

            Func<object> plan = this.buildPlanTable.GetValueOrDefault(t);
            return plan != null ? plan() : this.CompileAndRunPlan(t);
        }

        public IEnumerable<object> ResolveAll(Type t, params ResolverOverride[] resolverOverrides)
        {
            throw new NotSupportedException();
        }

        public void Teardown(object o)
        {
        }

        public void Dispose()
        {
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public object GetService(Type serviceType)
        {
#if DEBUG
            if (Logger.IsEnabled())
            {
                Logger.GetService(serviceType.ToString());
            }
#endif

            Func<object> plan = this.buildPlanTable.GetValueOrDefault(serviceType);
            return plan != null ? plan() : this.CompileAndRunPlan(serviceType);
        }

        public void AddBuildPlanVisitor(IBuildPlanVisitor visitor)
        {
            lock (this.lockObj)
            {
                this.buildPlanVisitors.Add(visitor);
                this.ClearBuildPlans();
            }
        }

        public void RegisterDependencyTreeListener(Action<ITreeNode<Type>> action)
        {
            this.dependencyTreeListener = action;
        }

        private void ClearBuildPlans()
        {
            this.buildPlanTable = ImmutableDictionary<Type, Func<object>>.Empty;
            Stack<QuickInjectContainer> childrenStack = new Stack<QuickInjectContainer>();
            childrenStack.Push(this);

            while (childrenStack.Count != 0)
            {
                var curr = childrenStack.Pop();
                curr.buildPlanTable = ImmutableDictionary<Type, Func<object>>.Empty;

                foreach (var child in curr.children)
                {
                    childrenStack.Push(child);
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private object CompileAndRunPlan(Type t)
        {
            Func<object> compiledexpression;

            lock (this.lockObj)
            {
                compiledexpression = this.buildPlanTable.GetValueOrDefault(t);
                if (compiledexpression == null)
                {
                    var depTree = t.BuildDependencyTree(this.Dependencies);
                    var typeRegistrationTree = new TypeRegistrationTree(this);
                    depTree.PreOrderTraverse(typeRegistrationTree.BuildRegistration);

                    var typeRegistrations = typeRegistrationTree.Parent;

                    var codeGenerator = new ExpressionGenerator(this, typeRegistrations);
                    var eptree = codeGenerator.Generate();

                    if (this.dependencyTreeListener != null)
                    {
                        this.dependencyTreeListener(depTree);
                    }

                    eptree = EmptyBlockExpressionRemovalVisitor.Visitor(eptree, t); // remove extra cruft
                    eptree = this.buildPlanVisitors.Aggregate(eptree, (current, visitor) => EmptyBlockExpressionRemovalVisitor.Visitor(visitor.Visitor(current, t), t)); // remove extra cruft after each visitor

                    compiledexpression = Expression.Lambda<Func<object>>(eptree, "Create_" + t, null).Compile();

                    this.buildPlanTable = this.buildPlanTable.AddOrUpdate(t, compiledexpression);
                }
            }

            return compiledexpression();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void SlowBuildUp(object existing, Type t)
        {
            var context = new DummyBuilderContext { BuildKey = new NamedTypeBuildKey(t) };
            var policyList = (DummyPolicyList)this.extensionImpl.Policies;

            if (policyList.PropertySelectorPolicy != null)
            {
                var selectedProperties = policyList.PropertySelectorPolicy.SelectProperties(context, policyList).Select(x => x.Property).ToArray();
                this.propertyInfoTable = this.propertyInfoTable.AddOrUpdate(t, selectedProperties);

                foreach (var selectedProperty in selectedProperties)
                {
                    selectedProperty.SetValue(existing, this.Resolve(selectedProperty.PropertyType, null, NoResolverOverrides));
                }
            }
        }

        private IEnumerable<Type> Dependencies(Type type)
        {
            Type mappedType = this.GetMappingFor(type);
            Expression expression = this.GetFactoryExpressionFor(mappedType);

            if (expression != null)
            {
                // container.RegisterType<IFoo>(new InjectionFactory(...))
                if (expression.GetType() == typeof(ParameterizedInjectionFactoryMethodCallExpression))
                {
                    return ((ParameterizedInjectionFactoryMethodCallExpression)expression).DependentTypes;
                }

                // container.RegisterType<IFoo>(new ParameterizedInjectionFactory<IProvider1, IProvider2, IFoo>(...))
                if (expression.GetType() == typeof(ParameterizedLambdaExpressionInjectionFactoryMethodCallExpression))
                {
                    return ((ParameterizedLambdaExpressionInjectionFactoryMethodCallExpression)expression).DependentTypes;
                }

                // Opaque, has no dependencies
                return Enumerable.Empty<Type>();
            }

            // container.RegisterType<IFoo>(new LifetimeManagerWillProvideValue())
            // Special Case: Func<T> that is not registered
            if ((mappedType.GetTypeInfo().IsGenericType && mappedType.GetGenericTypeDefinition().GetTypeInfo().BaseType == typeof(MulticastDelegate) || mappedType.GetTypeInfo().IsInterface || mappedType.GetTypeInfo().IsAbstract))
            {
                return Enumerable.Empty<Type>();
            }

            // Regular class that can be constructed
            return mappedType.ConstructorDependencies();
        }

        private LifetimeManager GetLifetimeFor(Type type)
        {
            LifetimeManager lifetime;

            if (this.lifetimeTable.TryGetValue(type, out lifetime))
            {
                return lifetime;
            }

            while (this.parentContainer != null)
            {
                return this.parentContainer.GetLifetimeFor(type);
            }

            return new TransientLifetimeManager();
        }

        private Type GetMappingFor(Type type)
        {
            Type mappedType;

            if (this.typeMappingTable.TryGetValue(type, out mappedType))
            {
                return mappedType;
            }

            while (this.parentContainer != null)
            {
                return this.parentContainer.GetMappingFor(type);
            }

            return type;
        }

        private Expression GetFactoryExpressionFor(Type type)
        {
            Expression expression;

            if (this.factoryExpressionTable.TryGetValue(type, out expression))
            {
                return expression;
            }

            while (this.parentContainer != null)
            {
                return this.parentContainer.GetFactoryExpressionFor(type);
            }

            return null;
        }

        private sealed class TypeRegistrationTree
        {
            private readonly QuickInjectContainer container;

            private ITreeNode<TypeRegistration> typeRegistrations;

            public TypeRegistrationTree(QuickInjectContainer container)
            {
                this.container = container;
            }

            public ITreeNode<TypeRegistration> Parent { get; private set; }

            public void BuildRegistration(Type type)
            {
                var mappedType = this.container.GetMappingFor(type);
                var typeRegistration = new TypeRegistration(type, mappedType, this.container.GetLifetimeFor(mappedType), this.container.GetFactoryExpressionFor(mappedType));
                if (this.Parent == null)
                {
                    this.typeRegistrations = new TreeNode<TypeRegistration>(typeRegistration);
                    this.Parent = this.typeRegistrations;
                }
                else
                {
                    this.typeRegistrations = this.typeRegistrations.AddChild(typeRegistration);
                }
            }
        }
    }
}