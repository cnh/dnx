// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Framework.DesignTimeHost.Models.IncomingMessages;
using Microsoft.Framework.DesignTimeHost.Models.OutgoingMessages;
using Microsoft.Framework.Runtime;
using Microsoft.Framework.Runtime.Common.DependencyInjection;

namespace Microsoft.Framework.DesignTimeHost
{
    public class PluginHandler
    {
        private const string RegisterPluginMessageName = "RegisterPlugin";
        private const string UnregisterPluginMessageName = "UnregisterPlugin";
        private const string PluginMessageMessageName = "PluginMessage";

        private readonly object _processLock = new object();
        private readonly Action<object> _sendMessageMethod;
        private readonly IServiceProvider _hostServices;
        private readonly Queue<PluginMessage> _faultedRegisterPluginMessages;
        private readonly ConcurrentQueue<PluginMessage> _messageQueue;
        private readonly IDictionary<string, IPlugin> _plugins;

        public PluginHandler(IServiceProvider hostServices, Action<object> sendMessageMethod)
        {
            _sendMessageMethod = sendMessageMethod;
            _hostServices = hostServices;
            _messageQueue = new ConcurrentQueue<PluginMessage>();
            _faultedRegisterPluginMessages = new Queue<PluginMessage>();
            _plugins = new Dictionary<string, IPlugin>(StringComparer.Ordinal);
        }

        public PluginMessageResult OnReceive(PluginMessage message)
        {
            var result = PluginMessageResult.Message;

            if (message.MessageName == RegisterPluginMessageName)
            {
                result = PluginMessageResult.PluginRegistration;
            }

            _messageQueue.Enqueue(message);

            return result;
        }

        public void Process(IAssemblyLoadContext assemblyLoadContext)
        {
            lock (_processLock)
            {
                TryRegisterFaultedPlugins(assemblyLoadContext);
                DrainMessages(assemblyLoadContext);
            }
        }

        private void TryRegisterFaultedPlugins(IAssemblyLoadContext assemblyLoadContext)
        {
            // Capture count here so when we enqueue later on we don't result in an infinite loop below.
            var faultedCount = _faultedRegisterPluginMessages.Count;

            for (var i = faultedCount; i > 0; i--)
            {
                var faultedRegisterPluginMessage = _faultedRegisterPluginMessages.Dequeue();
                var response = RegisterPlugin(faultedRegisterPluginMessage, assemblyLoadContext);

                if (response.Success)
                {
                    SendMessage(faultedRegisterPluginMessage.PluginId, response);
                }
                else
                {
                    // We were unable to recover, re-enqueue the faulted register plugin message.
                    _faultedRegisterPluginMessages.Enqueue(faultedRegisterPluginMessage);
                }
            }
        }

        private void DrainMessages(IAssemblyLoadContext assemblyLoadContext)
        {
            PluginMessage message;
            while (_messageQueue.TryDequeue(out message))
            {
                switch (message.MessageName)
                {
                    case RegisterPluginMessageName:
                        RegisterMessage(message, assemblyLoadContext);
                        break;
                    case UnregisterPluginMessageName:
                        UnregisterMessage(message);
                        break;
                    case PluginMessageMessageName:
                        PluginMessage(message);
                        break;
                }
            }
        }

        private void RegisterMessage(PluginMessage message, IAssemblyLoadContext assemblyLoadContext)
        {
            var response = RegisterPlugin(message, assemblyLoadContext);

            if (!response.Success)
            {
                _faultedRegisterPluginMessages.Enqueue(message);
            }

            SendMessage(message.PluginId, response);
        }

        private void UnregisterMessage(PluginMessage message)
        {
            if (!_plugins.Remove(message.PluginId))
            {
                OnError(
                    message.PluginId,
                    UnregisterPluginMessageName,
                    errorMessage: Resources.FormatPlugin_UnregisteredPluginIdCannotUnregister(message.PluginId));
            }
        }

        private void PluginMessage(PluginMessage message)
        {
            IPlugin plugin;
            if (_plugins.TryGetValue(message.PluginId, out plugin))
            {
                plugin.ProcessMessage(message.Data);
            }
            else
            {
                OnError(
                    message.PluginId,
                    PluginMessageMessageName,
                    errorMessage: Resources.FormatPlugin_UnregisteredPluginIdCannotReceiveMessages(message.PluginId));
            }
        }

        private PluginResponseMessage RegisterPlugin(
            PluginMessage message,
            IAssemblyLoadContext assemblyLoadContext)
        {
            var registerData = message.Data.ToObject<PluginRegisterData>();
            var response = new PluginResponseMessage
            {
                MessageName = RegisterPluginMessageName
            };

            Type pluginType = null;
            try
            {
                var assembly = assemblyLoadContext.Load(registerData.AssemblyName);
                pluginType = assembly.GetType(registerData.TypeName);
            }
            catch (Exception exception)
            {
                response.Error = exception.Message;

                return response;
            }

            if (pluginType != null)
            {
                var pluginId = message.PluginId;

                // We build out a custom plugin service provider to add a PluginMessageBroker and 
                // IAssemblyLoadContext to the potential services that can be used to construct an IPlugin.
                var pluginServiceProvider = new PluginServiceProvider(
                    _hostServices,
                    assemblyLoadContext,
                    messageBroker: new PluginMessageBroker(pluginId, _sendMessageMethod));

                var plugin = ActivatorUtilities.CreateInstance(pluginServiceProvider, pluginType) as IPlugin;

                if (plugin == null)
                {
                    response.Error = Resources.FormatPlugin_CannotProcessMessageInvalidPluginType(
                        pluginId,
                        pluginType.FullName,
                        typeof(IPlugin).FullName);

                    return response;
                }

                _plugins[pluginId] = plugin;
            }

            response.Success = true;

            return response;
        }

        private void SendMessage(string pluginId, PluginResponseMessage message)
        {
            var messageBroker = new PluginMessageBroker(pluginId, _sendMessageMethod);

            messageBroker.SendMessage(message);
        }

        private void OnError(string pluginId, string messageName, string errorMessage)
        {
            SendMessage(
                pluginId,
                message: new PluginResponseMessage
                {
                    MessageName = messageName,
                    Error = errorMessage,
                    Success = false
                });
        }

        private class PluginServiceProvider : IServiceProvider
        {
            private static readonly TypeInfo MessageBrokerTypeInfo = typeof(IPluginMessageBroker).GetTypeInfo();
            private static readonly TypeInfo AssemblyLoadContextTypeInfo = typeof(IAssemblyLoadContext).GetTypeInfo();
            private readonly IServiceProvider _hostServices;
            private readonly IAssemblyLoadContext _assemblyLoadContext;
            private readonly PluginMessageBroker _messageBroker;

            public PluginServiceProvider(
                IServiceProvider hostServices,
                IAssemblyLoadContext assemblyLoadContext,
                PluginMessageBroker messageBroker)
            {
                _hostServices = hostServices;
                _assemblyLoadContext = assemblyLoadContext;
                _messageBroker = messageBroker;
            }

            public object GetService(Type serviceType)
            {
                var hostProvidedService = _hostServices.GetService(serviceType);

                if (hostProvidedService == null)
                {
                    var serviceTypeInfo = serviceType.GetTypeInfo();

                    if (MessageBrokerTypeInfo.IsAssignableFrom(serviceTypeInfo))
                    {
                        return _messageBroker;
                    }
                    else if (AssemblyLoadContextTypeInfo.IsAssignableFrom(serviceTypeInfo))
                    {
                        return _assemblyLoadContext;
                    }
                }

                return hostProvidedService;
            }
        }

        private class PluginRegisterData
        {
            public string AssemblyName { get; set; }
            public string TypeName { get; set; }
        }
    }
}