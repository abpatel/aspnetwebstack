using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Dynamic;
using System.Linq;
using System.Net.Http.Formatting;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;
using System.Web.Http.Controllers;
using System.Web.Http.Metadata;
using System.Web.Http.OData;

namespace ASPNETMVC4WEBAPI
{
    internal interface IValueHandler
    {
        Type HandlerFor {get;}
        object Handle(object value);
        bool WasHandled { get; }
        void SetSuccessor(IValueHandler handler);
    }

    internal class PerRequestParameterBinding : HttpParameterBinding
    {
        private IEnumerable<MediaTypeFormatter> _formatters;

        public PerRequestParameterBinding(HttpParameterDescriptor descriptor,
            IEnumerable<MediaTypeFormatter> formatters)
            : base(descriptor)
        {
            if (formatters == null)
            {
                throw new ArgumentNullException("formatters");
            }

            _formatters = formatters;
        }

        public override bool WillReadBody
        {
            get
            {
                return true;
            }
        }

        public override Task ExecuteBindingAsync(
            ModelMetadataProvider metadataProvider, 
            HttpActionContext actionContext, 
            CancellationToken cancellationToken)
        {
            List<MediaTypeFormatter> perRequestFormatters = new List<MediaTypeFormatter>();

            foreach (MediaTypeFormatter formatter in _formatters)
            {
                MediaTypeFormatter perRequestFormatter = formatter.GetPerRequestFormatterInstance(Descriptor.ParameterType, actionContext.Request, actionContext.Request.Content.Headers.ContentType);
                perRequestFormatters.Add(perRequestFormatter);
            }

            HttpParameterBinding innerBinding = CreateInnerBinding(perRequestFormatters);
            Contract.Assert(innerBinding != null);

            return innerBinding.ExecuteBindingAsync(metadataProvider, actionContext, cancellationToken);
        }

        protected virtual HttpParameterBinding CreateInnerBinding(IEnumerable<MediaTypeFormatter> perRequestFormatters)
        {
            return Descriptor.BindWithFormatter(perRequestFormatters);
        }
    }

    internal sealed class NonValidatingParameterBindingAttribute : ParameterBindingAttribute
    {
        public override HttpParameterBinding GetBinding(HttpParameterDescriptor parameter)
        {
            IEnumerable<MediaTypeFormatter> formatters = parameter.Configuration.Formatters;

            return new NonValidatingParameterBinding(parameter, formatters);
        }

        private sealed class NonValidatingParameterBinding : PerRequestParameterBinding
        {
            public NonValidatingParameterBinding(HttpParameterDescriptor descriptor,
                IEnumerable<MediaTypeFormatter> formatters)
                : base(descriptor, formatters)
            {
            }

            protected override HttpParameterBinding CreateInnerBinding(IEnumerable<MediaTypeFormatter> perRequestFormatters)
            {
                return Descriptor.BindWithFormatter(perRequestFormatters, bodyModelValidator: null);
            }
        }
    }
    
    [NonValidatingParameterBinding]
    public class DeltaSlim<TEntityType> : DynamicObject, IDelta where TEntityType : class
    {
        private ConcurrentDictionary<Type,Dictionary<string,PropertyInfo> >
           _propertyCache = new ConcurrentDictionary<Type, Dictionary<string, PropertyInfo>>();
        private Dictionary<string, PropertyInfo> _propertiesThatExist = 
                            new Dictionary<string, PropertyInfo>();
        private HashSet<string> _changedProperties;
        private TEntityType _entity;
        private Type _entityType;
        /// <summary>
        /// Initializes a new instance of <see cref="Delta{TEntityType}"/>.
        /// </summary>
        public DeltaSlim()
            : this(typeof(TEntityType))
        {
        }

        /// <summary>
        /// Initializes a new instance of <see cref="Delta{TEntityType}"/>.
        /// </summary>
        /// <param name="entityType">The derived entity type for which the changes would be tracked.
        /// <paramref name="entityType"/> should be assignable to instances of <typeparamref name="TEntityType"/>.</param>
        public DeltaSlim(Type entityType)
        {
            Initialize(entityType);
        }

        private void Initialize(Type entityType)
        {
            if (entityType == null)
            {
                throw new ArgumentNullException("entityType");
            }

            if (!typeof(TEntityType).IsAssignableFrom(entityType))
            {
                throw new InvalidOperationException(string.Format("entity type {0} is not assignable to the Delta type {1}.", entityType, typeof(TEntityType)));
            }

            _entity = Activator.CreateInstance(entityType) as TEntityType;
            _changedProperties = new HashSet<string>();
            _entityType = entityType;
            _propertiesThatExist = InitializePropertiesThatExist();
        }

        private Dictionary<string, PropertyInfo> InitializePropertiesThatExist()
        {
            return _propertyCache.GetOrAdd(
                _entityType,
                (backingType) => backingType
                    .GetProperties()
                    .Where(p => p.GetSetMethod() != null && p.GetGetMethod() != null)
                    .Select(p => p)
                    .ToDictionary(p => p.Name));
        }

        private bool IsNullable(Type type)
        {
            return !type.IsValueType || Nullable.GetUnderlyingType(type) != null;
        }

        public override bool TryGetMember(GetMemberBinder binder, out object result)
        {
            if (binder == null)
            {
                throw new ArgumentNullException("binder");
            }

            return TryGetPropertyValue(binder.Name, out result);
        }

        public override bool TrySetMember(SetMemberBinder binder, object value)
        {
            if (binder == null)
            {
                throw new ArgumentNullException("binder");
            }

            return TrySetPropertyValue(binder.Name, value);
        }

        public void Clear()
        {
            Initialize(_entityType);
        }

        public TEntityType GetEntity()
        {
            return _entity;
        }

        public IEnumerable<string> GetChangedPropertyNames()
        {
            return _changedProperties;
        }

        public IEnumerable<string> GetUnchangedPropertyNames()
        {
            return _propertiesThatExist.Keys.Except(GetChangedPropertyNames());
        }

        public bool TryGetPropertyType(string name, out Type type)
        {
            if (name == null)
            {
                throw new ArgumentNullException("name");
            }

            PropertyInfo value;
            if (_propertiesThatExist.TryGetValue(name, out value))
            {
                type = value.PropertyType;
                return true;
            }
            else
            {
                type = null;
                return false;
            }
        }

        public bool TryGetPropertyValue(string name, out object value)
        {
            if (name == null)
            {
                throw new ArgumentNullException("name");
            }

            if (_propertiesThatExist.ContainsKey(name))
            {
                PropertyInfo cacheHit = _propertiesThatExist[name];
                value = cacheHit.GetValue(_entity);
                return true;
            }
            else
            {
                value = null;
                return false;
            }
        }

        public bool TrySetPropertyValue(string name, object value)
        {
            if (name == null)
            {
                throw new ArgumentNullException("name");
            }

            if (!_propertiesThatExist.ContainsKey(name))
            {
                return false;
            }

            PropertyInfo cacheHit = _propertiesThatExist[name];

            if (value == null && !IsNullable(cacheHit.PropertyType))
            {
                return false;
            }

            if (value != null && !cacheHit.PropertyType.IsAssignableFrom(value.GetType()))
            {
                if (cacheHit.PropertyType.IsEnum)
                {
                    if (value.GetType() == typeof(string))
                    {
                        string valueString = value.ToString();
                        if (Enum.GetNames(cacheHit.PropertyType).Contains(valueString))
                        {
                            object enumeratedObject = Enum.Parse(cacheHit.PropertyType, valueString);
                            value = Convert.ChangeType(enumeratedObject,
                                Enum.GetUnderlyingType(cacheHit.PropertyType));
                        }
                        else
                        {
                            return false;
                        }
                    }
                    else
                    {
                        try
                        {
                            value = Convert.ChangeType(value,
                                Enum.GetUnderlyingType(cacheHit.PropertyType));
                            if (!Enum.IsDefined(cacheHit.PropertyType, value))
                            {
                                return false;
                            }
                        }
                        catch
                        {
                            return false;
                        }
                    }
                }
                else
                {
                    Type t = Nullable.GetUnderlyingType(cacheHit.PropertyType) ??
                        cacheHit.PropertyType;
                    try
                    {
                        value = Convert.ChangeType(value, t);
                    }
                    catch
                    {
                        return false;
                    }
                }
            }

            //.Setter.Invoke(_entity, new object[] { value });
            cacheHit.SetValue(_entity, value);
            _changedProperties.Add(name);
            return true;
        }

        /// <summary>
        /// Copies the changed property values from the underlying entity (accessible via <see cref="GetEntity()" />) 
        /// to the <paramref name="original"/> entity.
        /// </summary>
        /// <param name="original">The entity to be updated.</param>
        public void CopyChangedValues(TEntityType original)
        {
            if (original == null)
            {
                throw new ArgumentNullException("original");
            }

            if (!_entityType.IsAssignableFrom(original.GetType()))
            {
                throw new InvalidOperationException(string.Format("entity type {0} is not assignable to the Delta type {1}.", _entityType, typeof(TEntityType)));

                throw new ArgumentException("original", string.Format("Cannot use Delta of type {0} on an entity of type {1}.", _entityType, original.GetType()));
            }

            PropertyInfo[] propertiesToCopy = GetChangedPropertyNames().Select(s => _propertiesThatExist[s]).ToArray();
            foreach (PropertyInfo propertyToCopy in propertiesToCopy)
            {
                propertyToCopy.SetValue(_entity, original);
            }
        }

        /// <summary>
        /// Copies the unchanged property values from the underlying entity (accessible via <see cref="GetEntity()" />) 
        /// to the <paramref name="original"/> entity.
        /// </summary>
        /// <param name="original">The entity to be updated.</param>
        public void CopyUnchangedValues(TEntityType original)
        {
            if (original == null)
            {
                throw new ArgumentNullException("original");
            }

            if (!_entityType.IsAssignableFrom(original.GetType()))
            {
                throw new ArgumentException("original", string.Format("Cannot use Delta of type {0} on an entity of type {1}.", _entityType, original.GetType()));
            }

            PropertyInfo[] propertiesToCopy = GetUnchangedPropertyNames().Select(s => _propertiesThatExist[s]).ToArray();
            foreach (PropertyInfo propertyToCopy in propertiesToCopy)
            {
                propertyToCopy.SetValue(_entity, original);
            }
        }

        /// <summary>
        /// Overwrites the <paramref name="original"/> entity with the changes tracked by this Delta.
        /// <remarks>The semantics of this operation are equivalent to a HTTP PATCH operation, hence the name.</remarks>
        /// </summary>
        /// <param name="original">The entity to be updated.</param>
        public void Patch(TEntityType original)
        {
            CopyChangedValues(original);
        }

        /// <summary>
        /// Overwrites the <paramref name="original"/> entity with the values stored in this Delta.
        /// <remarks>The semantics of this operation are equivalent to a HTTP PUT operation, hence the name.</remarks>
        /// </summary>
        /// <param name="original">The entity to be updated.</param>
        public void Put(TEntityType original)
        {
            CopyChangedValues(original);
            CopyUnchangedValues(original);
        }
    }
}