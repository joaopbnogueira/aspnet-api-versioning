using System.Reflection;

namespace System.Web.Http
{
    using Collections.Generic;
    using Collections.Specialized;
    using Diagnostics.CodeAnalysis;
    using Diagnostics.Contracts;
    using Linq;
    using Microsoft;
    using Microsoft.OData.Edm;
    using Microsoft.Web.Http;
    using Microsoft.Web.Http.Routing;
    using Microsoft.Web.OData.Builder;
    using Microsoft.Web.OData.Routing;
    using Microsoft.OData;    
    using Microsoft.OData.UriParser;
    using OData.Batch;
    using OData.Extensions;
    using OData.Routing;
    using OData.Routing.Conventions;
    using Routing;
    using Microsoft.Extensions.DependencyInjection;
    using static System.String;
    using static System.StringComparison;
    using ServiceLifetime = Microsoft.OData.ServiceLifetime;
    using System.Net.Http;
    using System.Web.OData;

    /// <summary>
    /// Provides extension methods for the <see cref="HttpConfiguration"/> class.
    /// </summary>
    public static class HttpConfigurationExtensions
    {        
        private const string UnversionedRouteSuffix = "-Unversioned";
        private const string ApiVersionConstraintName = "apiVersion";
        private const string ApiVersionConstraint = "{" + ApiVersionConstraintName + "}";        

        private static IList<IODataRoutingConvention> EnsureConventions( IList<IODataRoutingConvention> conventions )
        {
            Contract.Requires( conventions != null );
            Contract.Ensures( Contract.Result<IList<IODataRoutingConvention>>() != null );

            var discovered = new BitVector32( 0 );

            for ( var i = 0; i < conventions.Count; i++ )
            {
                var convention = conventions[i];

                if ( convention is MetadataRoutingConvention )
                {
                    conventions[i] = new VersionedMetadataRoutingConvention();
                    discovered[1] = true;
                }
                else if ( convention is VersionedMetadataRoutingConvention )
                {
                    discovered[1] = true;
                }
            }

            if ( !discovered[1] )
            {
                conventions.Insert( 0, new VersionedMetadataRoutingConvention() );
            }

            return conventions;
        }

        /// <summary>
        /// Maps the specified versioned OData routes.
        /// </summary>
        /// <param name="configuration">The extended <see cref="HttpConfiguration">HTTP configuration</see>.</param>
        /// <param name="routeName">The name of the route to map.</param>
        /// <param name="routePrefix">The prefix to add to the OData route's path template.</param>
        /// <param name="containerBuilderAction">The container builder action used for DI in OData Lib V6+.</param>
        /// <param name="models">  The <see cref="IEnumerable{T}">sequence</see> of <see cref="IEdmModel">EDM models</see> to use for parsing OData paths.</param>
        /// <returns>The <see cref="IReadOnlyList{T}">read-only list</see> of added <see cref="ODataRoute">OData routes</see>.</returns>
        /// <remarks>The specified <paramref name="models"/> must contain the <see cref="ApiVersionAnnotation">API version annotation</see>.  This annotation is
        /// automatically applied when you use the <see cref="VersionedODataModelBuilder"/> and call <see cref="VersionedODataModelBuilder.GetEdmModels"/> to
        /// create the <paramref name="models"/>.</remarks>
        public static IReadOnlyList<ODataRoute> MapVersionedODataRoutes(this HttpConfiguration configuration, string routeName, string routePrefix, Action<IContainerBuilder> containerBuilderAction, IEnumerable<IEdmModel> models)
        {
            Arg.NotNull(configuration, nameof(configuration));
            Arg.NotNull(routeName, nameof(routeName));

            var serviceProvider = GetODataServiceProvider(containerBuilderAction);

            var routingConventions = serviceProvider.GetServices<IODataRoutingConvention>() ?? ODataRoutingConventions.CreateDefaultWithAttributeRouting(routePrefix, configuration);

            var batchHandler = serviceProvider.GetService<ODataBatchHandler>();
            if (batchHandler != null)
            {
                var batchTemplate = IsNullOrEmpty(routePrefix) ? ODataRouteConstants.Batch : routePrefix + '/' + ODataRouteConstants.Batch;
                configuration.Routes.MapHttpBatchRoute(routeName + "Batch", batchTemplate, batchHandler);
            }

            var odataRoutes = new List<ODataRoute>();
            var unversionedConstraints = new List<IHttpRouteConstraint>();
            var routeConventions = EnsureConventions(routingConventions.ToList());
            routeConventions.Insert(0, null);
            foreach (var model in models)
            {
                var versionedRouteName = routeName;

                routeConventions[0] = new VersionedAttributeRoutingConvention(model, routeName, configuration);

                // Adjust OData path constraints & get new route name
                var routeConstraint = new ODataPathRouteConstraint(versionedRouteName);
                unversionedConstraints.Add(routeConstraint);
                routeConstraint = MakeVersionedODataRouteConstraint(routeConstraint, model, ref versionedRouteName);

                // Build Route               
                var routeContainerBuilderAction = BuildContainerBuilderActionForRoute(serviceProvider, model, routeConventions);
                var route = configuration.MapODataServiceRoute(versionedRouteName, routePrefix, routeContainerBuilderAction);
                AddApiVersionConstraintIfNecessary(route);
                EnsureODataPathRouteConstraint(route, routeConstraint);
                odataRoutes.Add(route);
            }

            AddRouteToRespondWithBadRequestWhenAtLeastOneRouteCouldMatch(routeName, routePrefix, configuration.Routes, odataRoutes, unversionedConstraints);

            return odataRoutes;
        }                

        /// <summary>
        /// Maps the specified versioned OData routes. When the <paramref name="batchHandler"/> is provided, it will create a '$batch' endpoint to handle the batch requests.
        /// </summary>
        /// <param name="configuration">The extended <see cref="HttpConfiguration">HTTP configuration</see>.</param>
        /// <param name="routeName">The name of the route to map.</param>
        /// <param name="routePrefix">The prefix to add to the OData route's path template.</param>
        /// <param name="models">The <see cref="IEnumerable{T}">sequence</see> of <see cref="IEdmModel">EDM models</see> to use for parsing OData paths.</param>
        /// <param name="pathHandler">The <see cref="IODataPathHandler">OData path handler</see> to use for parsing the OData path.</param>
        /// <param name="routingConventions">The <see cref="IEnumerable{T}">sequence</see> of <see cref="IODataRoutingConvention">OData routing conventions</see>
        /// to use for controller and action selection.</param>
        /// <param name="batchHandler">The <see cref="ODataBatchHandler">OData batch handler</see>.</param>
        /// <returns>The <see cref="IReadOnlyList{T}">read-only list</see> of added <see cref="ODataRoute">OData routes</see>.</returns>
        /// <remarks>The specified <paramref name="models"/> must contain the <see cref="ApiVersionAnnotation">API version annotation</see>.  This annotation is
        /// automatically applied when you use the <see cref="VersionedODataModelBuilder"/> and call <see cref="VersionedODataModelBuilder.GetEdmModels"/> to
        /// create the <paramref name="models"/>.</remarks>
        [SuppressMessage( "Microsoft.Design", "CA1062:Validate arguments of public methods", MessageId = "0", Justification = "Validated by a code contract." )]
        [SuppressMessage( "Microsoft.Design", "CA1062:Validate arguments of public methods", MessageId = "3", Justification = "Validated by a code contract." )]
        [SuppressMessage( "Microsoft.Design", "CA1011:ConsiderPassingBaseTypesAsParameters", Justification = "The specified handler must be the batch handler." )]
        public static IReadOnlyList<ODataRoute> MapVersionedODataRoutes(
            this HttpConfiguration configuration,
            string routeName,
            string routePrefix,
            IEnumerable<IEdmModel> models,
            IODataPathHandler pathHandler = null,
            IEnumerable<IODataRoutingConvention> routingConventions = null,
            ODataBatchHandler batchHandler = null)
        {
            var odataContainerBuilder = CreateOdataContainerBuilderFromParameters(pathHandler, routingConventions, batchHandler);

            return configuration.MapVersionedODataRoutes(routeName, routePrefix, odataContainerBuilder, models);
        }

        /// <summary>
        /// Maps a versioned OData route.
        /// </summary>
        /// <param name="configuration">The extended <see cref="HttpConfiguration">HTTP configuration</see>.</param>
        /// <param name="routeName">The name of the route to map.</param>
        /// <param name="routePrefix">The prefix to add to the OData route's path template.</param>
        /// <param name="model">The <see cref="IEdmModel">EDM model</see> to use for parsing OData paths.</param>
        /// <param name="apiVersion">The <see cref="ApiVersion">API version</see> associated with the model.</param>
        /// <param name="containerBuilderAction">The container builder action used for DI in OData Lib V6+.</param>
        /// <returns>The mapped <see cref="ODataRoute">OData route</see>.</returns>
        /// <remarks>The <see cref="ApiVersionAnnotation">API version annotation</see> will be added or updated on the specified <paramref name="model"/> using
        /// the provided <paramref name="apiVersion">API version</paramref>.</remarks>
        public static ODataRoute MapVersionedODataRoute(this HttpConfiguration configuration,
            string routeName,
            string routePrefix,
            IEdmModel model,
            ApiVersion apiVersion, Action<IContainerBuilder> containerBuilderAction)
        {
            var serviceProvider = GetODataServiceProvider(containerBuilderAction);

            var routingConventions = serviceProvider.GetServices<IODataRoutingConvention>() ?? ODataRoutingConventions.CreateDefaultWithAttributeRouting(routePrefix, configuration);
            var routeConventions = EnsureConventions(routingConventions.ToList());

            model.SetAnnotationValue(model, new ApiVersionAnnotation(apiVersion));
            routeConventions.Insert(0, new VersionedAttributeRoutingConvention(model, routeName, configuration));

            var routeConstraint = new VersionedODataPathRouteConstraint(model, routeName, apiVersion);

            var routeContainerBuilderAction = BuildContainerBuilderActionForRoute(serviceProvider, model, routeConventions);
            var route = configuration.MapODataServiceRoute(routeName, routePrefix, routeContainerBuilderAction);
            AddApiVersionConstraintIfNecessary(route);
            EnsureODataPathRouteConstraint(route, routeConstraint);

            var unversionedRouteConstraint = new ODataPathRouteConstraint(routeName);
            var unversionedRoute = new ODataRoute(routePrefix, new UnversionedODataPathRouteConstraint(unversionedRouteConstraint, apiVersion));

            AddApiVersionConstraintIfNecessary(unversionedRoute);
            configuration.Routes.Add(routeName + UnversionedRouteSuffix, unversionedRoute);

            return route;
        }

        /// <summary>
        /// Maps a versioned OData route. When the <paramref name="batchHandler"/> is provided, it will create a '$batch' endpoint to handle the batch requests.
        /// </summary>
        /// <param name="configuration">The extended <see cref="HttpConfiguration">HTTP configuration</see>.</param>
        /// <param name="routeName">The name of the route to map.</param>
        /// <param name="routePrefix">The prefix to add to the OData route's path template.</param>
        /// <param name="model">The <see cref="IEdmModel">EDM model</see> to use for parsing OData paths.</param>
        /// <param name="apiVersion">The <see cref="ApiVersion">API version</see> associated with the model.</param>
        /// <param name="pathHandler">The <see cref="IODataPathHandler">OData path handler</see> to use for parsing the OData path.</param>
        /// <param name="routingConventions">The <see cref="IEnumerable{T}">sequence</see> of <see cref="IODataRoutingConvention">OData routing conventions</see>
        /// to use for controller and action selection.</param>
        /// <param name="batchHandler">The <see cref="ODataBatchHandler">OData batch handler</see>.</param>
        /// <returns>The mapped <see cref="ODataRoute">OData route</see>.</returns>
        /// <remarks>The <see cref="ApiVersionAnnotation">API version annotation</see> will be added or updated on the specified <paramref name="model"/> using
        /// the provided <paramref name="apiVersion">API version</paramref>.</remarks>
        [SuppressMessage( "Microsoft.Design", "CA1062:Validate arguments of public methods", MessageId = "0", Justification = "Validated by a code contract." )]
        [SuppressMessage( "Microsoft.Design", "CA1011:ConsiderPassingBaseTypesAsParameters", Justification = "The specified handler must be the batch handler." )]
        public static ODataRoute MapVersionedODataRoute(this HttpConfiguration configuration,
            string routeName,
            string routePrefix,
            IEdmModel model,
            ApiVersion apiVersion,
            IODataPathHandler pathHandler = null,
            IEnumerable<IODataRoutingConvention> routingConventions = null,
            ODataBatchHandler batchHandler = null)
        {
            var odataContainerBuilder = CreateOdataContainerBuilderFromParameters(pathHandler, routingConventions, batchHandler);

            return configuration.MapVersionedODataRoute(routeName, routePrefix, model, apiVersion, odataContainerBuilder);
        }

        private static ODataPathRouteConstraint MakeVersionedODataRouteConstraint(ODataPathRouteConstraint routeConstraint, IEdmModel model, ref string versionedRouteName)
        {
            Contract.Requires(routeConstraint != null);
            Contract.Requires(model != null);
            Contract.Requires(!IsNullOrEmpty(versionedRouteName));
            Contract.Ensures(Contract.Result<ODataPathRouteConstraint>() != null);

            var apiVersion = model.GetAnnotationValue<ApiVersionAnnotation>(model)?.ApiVersion;

            if (apiVersion == null)
            {
                return routeConstraint;
            }

            versionedRouteName += "-" + apiVersion.ToString();
            return new VersionedODataPathRouteConstraint(model, versionedRouteName, apiVersion);
        }

        private static void EnsureODataPathRouteConstraint(ODataRoute route, ODataPathRouteConstraint routeConstraint)
        {
            var originalConstraints = new Dictionary<string, object>(route.Constraints);

            route.Constraints.Clear();
            foreach (var constraint in originalConstraints)
            {
                var constraintValue = constraint.Value;
                if (constraintValue.GetType() == typeof(ODataPathRouteConstraint))
                {
                    constraintValue = routeConstraint;
                    route.SetPrivatePropertyValue("PathRouteConstraint", routeConstraint);
                    route.SetPrivatePropertyValue("RouteConstraint", (IHttpRouteConstraint)routeConstraint);
                }
                route.Constraints.Add(constraint.Key, constraintValue);
            }
        }


        /// <summary>
        /// Sets the private property value using reflection
        /// </summary>
        /// <typeparam name="T">Type da property</typeparam>
        /// <param name="obj">Objecto onde a property está</param>
        /// <param name="propName">Nome da property</param>
        /// <param name="val">Valor a settar</param>
        /// <exception cref="System.ArgumentOutOfRangeException">propName</exception>
        private static void SetPrivatePropertyValue<T>(this object obj, string propName, T val)
        {
            Type t = obj.GetType();
            if (t.GetProperty(propName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) == null)
                throw new ArgumentOutOfRangeException("propName", string.Format("Property {0} was not found in Type {1}", propName, obj.GetType().FullName));
            t.InvokeMember(propName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.SetProperty | BindingFlags.Instance, null, obj, new object[] { val });
        }

        private static Action<IContainerBuilder> CreateOdataContainerBuilderFromParameters(IODataPathHandler pathHandler, IEnumerable<IODataRoutingConvention> routingConventions, ODataBatchHandler batchHandler)
        {
            Action<IContainerBuilder> odataContainerBuilder = builder =>
            {
                if (pathHandler != null)
                {
                    builder.AddService(ServiceLifetime.Singleton, sp => pathHandler);
                }

                if (routingConventions != null)
                {
                    builder.AddService(ServiceLifetime.Singleton, sp => routingConventions.AsEnumerable());
                }

                if (batchHandler != null)
                {
                    builder.AddService(ServiceLifetime.Singleton, sp => batchHandler);
                }
            };
            return odataContainerBuilder;
        }

        private static Action<IContainerBuilder> BuildContainerBuilderActionForRoute(IServiceProvider serviceProvider, IEdmModel edmModel, IList<IODataRoutingConvention> routingConventions)
        {
            Action<IContainerBuilder> odataContainerBuilder = builder =>
            {
                builder.AddService(ServiceLifetime.Singleton, sp => edmModel);

                var oDataPathHandler = serviceProvider.GetService<IODataPathHandler>() ?? new DefaultODataPathHandler();
                builder.AddService(ServiceLifetime.Singleton, sp => oDataPathHandler);

                if (routingConventions != null)
                {
                    builder.AddService(ServiceLifetime.Singleton, sp => routingConventions.AsEnumerable());
                }

                var httpMessageHandler = serviceProvider.GetService<HttpMessageHandler>();
                if (httpMessageHandler != null)
                {
                    builder.AddService(ServiceLifetime.Singleton, sp => httpMessageHandler);
                }

                var oDataUriResolver = serviceProvider.GetService<ODataUriResolver>();
                if (oDataUriResolver != null)
                {
                    builder.AddService(ServiceLifetime.Singleton, sp => oDataUriResolver);
                }

                var oDataBatchHandler = serviceProvider.GetService<ODataBatchHandler>();
                if (oDataBatchHandler != null)
                {
                    builder.AddService(ServiceLifetime.Singleton, sp => oDataBatchHandler);
                }
            };

            return odataContainerBuilder;
        }

        private static IServiceProvider GetODataServiceProvider(Action<IContainerBuilder> containerBuilderAction)
        {
            var builder = new DefaultContainerBuilder();
            containerBuilderAction?.Invoke(builder);
            return builder.BuildContainer();
        }

        private static void AddApiVersionConstraintIfNecessary( ODataRoute route )
        {
            Contract.Requires( route != null );

            var routePrefix = route.RoutePrefix;

            if ( routePrefix == null || routePrefix.IndexOf( ApiVersionConstraint, Ordinal ) < 0 || route.Constraints.ContainsKey( ApiVersionConstraintName ) )
            {
                return;
            }

            // note: even though the constraints are a dictionary, it's important to rebuild the entire collection
            // to make sure the api version constraint is evaluated first; otherwise, the current api version will
            // not be resolved when the odata versioning constraint is evaluated
            var originalConstraints = new Dictionary<string, object>( route.Constraints );

            route.Constraints.Clear();
            route.Constraints.Add( ApiVersionConstraintName, new ApiVersionRouteConstraint() );

            foreach ( var constraint in originalConstraints )
            {
                route.Constraints.Add( constraint.Key, constraint.Value );
            }
        }

        private static void AddRouteToRespondWithBadRequestWhenAtLeastOneRouteCouldMatch( string routeName, string routePrefix, HttpRouteCollection routes, List<ODataRoute> odataRoutes, List<IHttpRouteConstraint> unversionedConstraints )
        {
            Contract.Requires( !IsNullOrEmpty( routeName ) );
            Contract.Requires( routes != null );
            Contract.Requires( odataRoutes != null );
            Contract.Requires( unversionedConstraints != null );

            var unversionedRoute = new ODataRoute( routePrefix, new UnversionedODataPathRouteConstraint( unversionedConstraints ) );

            AddApiVersionConstraintIfNecessary( unversionedRoute );
            routes.Add( routeName + UnversionedRouteSuffix, unversionedRoute );
            odataRoutes.Add( unversionedRoute );
        }
    }
}