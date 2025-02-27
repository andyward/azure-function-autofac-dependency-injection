﻿using Autofac;
using AzureFunctions.Autofac.Exceptions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

namespace AzureFunctions.Autofac.Configuration
{
    public static class DependencyInjection
    {
        /// <summary>
        /// The global set of containers, which can be shared across functions
        /// </summary>
        private static ConcurrentDictionary<object, IContainer> rootContainers = new ConcurrentDictionary<object, IContainer>();

        private static ConcurrentDictionary<string, IContainer> functionContainers = new ConcurrentDictionary<string, IContainer>();
        private static ConcurrentDictionary<Guid, ILifetimeScope> instanceContainers = new ConcurrentDictionary<Guid, ILifetimeScope>();

        private static ConcurrentDictionary<string, bool> _enableCaching = new ConcurrentDictionary<string, bool>();
        private static ConcurrentDictionary<string, Func<IContainer>> nonCachedContainerBuilder = new ConcurrentDictionary<string, Func<IContainer>>();

        private static IContainer SetupContainerBuilder(Action<ContainerBuilder> cfg, Action<IContainer> containerAction = null)
        {
            ContainerBuilder builder = new ContainerBuilder();
            cfg(builder);
            var container = builder.Build();
            containerAction?.Invoke(container);
            return container;
        }

        public static void Initialize(Action<ContainerBuilder> cfg, string functionClassName, Action<IContainer> containerAction = null, bool enableCaching = true)
        {
            _enableCaching[functionClassName] = enableCaching;
            if (_enableCaching[functionClassName])
            {
                functionContainers.GetOrAdd(functionClassName, str =>
                {
                    return rootContainers.GetOrAdd(cfg.Target, method => SetupContainerBuilder(cfg, containerAction));
                });
            }
            else
            {
                nonCachedContainerBuilder.GetOrAdd(functionClassName, () => SetupContainerBuilder(cfg, containerAction));
            }
        }

        public static object Resolve(Type type, string name, string functionClassName, Guid functionInstanceId)
        {
            if (functionContainers.ContainsKey(functionClassName) || (!_enableCaching[functionClassName] && nonCachedContainerBuilder.ContainsKey(functionClassName)))
            {
                IContainer container;
                if (_enableCaching[functionClassName])
                {
                    container = functionContainers[functionClassName];
                }
                else
                {
                    container = nonCachedContainerBuilder[functionClassName].Invoke();
                }

                var scope = instanceContainers.GetOrAdd(functionInstanceId, id => container.BeginLifetimeScope());

                object resolved = null;
                if (string.IsNullOrWhiteSpace(name))
                {
                    resolved = scope.Resolve(type);
                }
                else
                {
                    resolved = scope.ResolveNamed(name, type);
                }
                return resolved;
            }
            else
            {
                throw new InitializationException("DependencyInjection.Initialize must be called before dependencies can be resolved.");
            }
        }

        public static void RemoveScope(Guid functionInstanceId)
        {
            if (instanceContainers.TryRemove(functionInstanceId, out ILifetimeScope scope))
            {
                scope.Dispose();
            };
        }

        /// <summary>
        /// Verifies that the depency injection is set up properly for the given type. Searches for public static functions.
        /// Verifies the following things:
        /// * That an InjectAttribute on a parameter, has a matching DependencyInjectionConfigAttribute on the class.
        /// * That the configuration can be constructed with a string-parameter.
        /// * That the injected parameters can be resolved using the given configuration.
        /// * Optionally that a DependencyInjectionConfigAttribute has at least one InjectAttribute on a method.
        /// </summary>
        /// <param name="type">The type to verify.</param>
        /// <param name="verifyUnnecessaryConfig">If true, verify that no configuration exists unless there is at least one injected parameter. Defaults to true.</param>
        public static void VerifyConfiguration(Type type, bool verifyUnnecessaryConfig = true, string appDirectory = null, ILoggerFactory loggerFactory = null)
        {
            var configAttr = type.GetCustomAttribute<DependencyInjectionConfigAttribute>();
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
            var injectAttrFound = false;

            foreach (var method in methods)
            {
                foreach (var param in method.GetParameters())
                {
                    var injectAttr = param.GetCustomAttribute<InjectAttribute>();

                    if (injectAttr == null)
                    {
                        continue;
                    }

                    if (configAttr == null)
                    {
                        throw new MissingAttributeException($"{nameof(InjectAttribute)} used without {nameof(DependencyInjectionConfigAttribute)}");
                    }

                    injectAttrFound = true;
                    var functionName = $"testfunction-{Guid.NewGuid()}";


                    //Initialize DependencyInjection
                    var functionAndAppDirectoryAndLoggerFactoryConstructor = configAttr.Config.GetConstructor(new[] { typeof(string), typeof(string), typeof(ILoggerFactory) });
                    var functionAndAppLoggerFactoryConstructor = configAttr.Config.GetConstructor(new[] { typeof(string), typeof(ILoggerFactory) });
                    var functionAndAppDirectoryConstructor = configAttr.Config.GetConstructor(new[] { typeof(string), typeof(string) });

                    if (functionAndAppDirectoryAndLoggerFactoryConstructor != null)
                    {
                        Activator.CreateInstance(configAttr.Config, functionName, appDirectory, loggerFactory);
                    }
                    else if (functionAndAppDirectoryConstructor != null)
                    {
                        Activator.CreateInstance(configAttr.Config, functionName, appDirectory);
                    }
                    else if (functionAndAppLoggerFactoryConstructor != null)
                    {
                        Activator.CreateInstance(configAttr.Config, functionName, loggerFactory);
                    }
                    else
                    {
                        Activator.CreateInstance(configAttr.Config, functionName);
                    }

                    var functionInstanceId = Guid.NewGuid();
                    Resolve(param.ParameterType, injectAttr.Name, functionName, functionInstanceId);
                }
            }

            if (!injectAttrFound && configAttr != null && verifyUnnecessaryConfig)
            {
                throw new MissingAttributeException($"{nameof(DependencyInjectionConfigAttribute)} used without {nameof(InjectAttribute)}");
            }
        }
    }
}
