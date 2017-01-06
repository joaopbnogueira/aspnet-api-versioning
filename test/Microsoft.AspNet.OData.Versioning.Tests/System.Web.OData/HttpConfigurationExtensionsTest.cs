using System.Collections;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData;
using ServiceLifetime = Microsoft.OData.ServiceLifetime;

namespace System.Web.OData
{
    using Batch;
    using Builder;
    using Collections.Generic;
    using FluentAssertions;
    using Http;
    using Http.Dispatcher;
    using Linq;
    using Microsoft.OData.Edm;
    using Microsoft.Web.Http;
    using Microsoft.Web.OData.Builder;
    using Microsoft.Web.OData.Routing;
    using Moq;
    using Routing.Conventions;
    using Xunit;

    public class HttpConfigurationExtensionsTest
    {

        private const string RootContainerMappingsKey = "System.Web.OData.RootContainerMappingsKey";

        [ApiVersion( "1.0" )]
        private sealed class ControllerV1 : ODataController
        {
        }

        [ApiVersion( "2.0" )]
        private sealed class ControllerV2 : ODataController
        {
        }

        private static IEnumerable<IEdmModel> CreateModels( HttpConfiguration configuration )
        {
            var controllerTypeResolver = new Mock<IHttpControllerTypeResolver>();
            var controllerTypes = new List<Type>() { typeof( ControllerV1 ), typeof( ControllerV2 ) };

            controllerTypeResolver.Setup( ctr => ctr.GetControllerTypes( It.IsAny<IAssembliesResolver>() ) ).Returns( controllerTypes );
            configuration.Services.Replace( typeof( IHttpControllerTypeResolver ), controllerTypeResolver.Object );

            var builder = new VersionedODataModelBuilder( configuration );

            return builder.GetEdmModels();
        }

        [Fact]
        public void map_versioned_odata_routes_should_return_expected_result()
        {
            // arrange
            try
            {
                var configuration = new HttpConfiguration();
                var httpServer = new HttpServer(configuration);
                var routeName = "odata";
                var routePrefix = "api/v3";
                var model = new ODataModelBuilder().GetEdmModel();
                var apiVersion = new ApiVersion(3, 0);
                var batchHandler = new DefaultODataBatchHandler(httpServer);
                Action<IContainerBuilder> odataContainerBuilder = (builder => builder.AddService<ODataBatchHandler>(ServiceLifetime.Singleton, sp => batchHandler));

                // act
                var route = configuration.MapVersionedODataRoute(routeName, routePrefix, model, apiVersion, odataContainerBuilder);
                var constraint = (VersionedODataPathRouteConstraint)route.PathRouteConstraint;
                var batchRoute = configuration.Routes["odataBatch"];

                var serviceProviders = (ConcurrentDictionary<string, IServiceProvider>)configuration.Properties[RootContainerMappingsKey];
                var serviceProvider = serviceProviders[routeName];
                var routingConventions = serviceProvider.GetServices<IODataRoutingConvention>().ToArray();

                // assert
                routingConventions[0].Should().BeOfType<VersionedAttributeRoutingConvention>();
                routingConventions[1].Should().BeOfType<VersionedMetadataRoutingConvention>();
                routingConventions.OfType<MetadataRoutingConvention>().Should().BeEmpty();
                constraint.RouteName.Should().Be(routeName);
                route.RoutePrefix.Should().Be(routePrefix);
                batchRoute.Handler.Should().Be(batchHandler);
                batchRoute.RouteTemplate.Should().Be("api/v3/$batch");
            }
            catch (Exception exception)
            {
                Console.Write(exception);
                throw;
            }
        }

        [Fact]
        public void map_versioned_odata_routes_should_return_expected_results()
        {
            // arrange
            var configuration = new HttpConfiguration();
            var httpServer = new HttpServer( configuration );
            var routeName = "odata";
            var routePrefix = "api";
            var batchHandler = new DefaultODataBatchHandler( httpServer );
            var models = CreateModels( configuration );

            // act
            Action<IContainerBuilder> odataContainerBuilder = (builder => builder.AddService<ODataBatchHandler>(ServiceLifetime.Singleton, sp => batchHandler));
            var routes = configuration.MapVersionedODataRoutes( routeName, routePrefix, odataContainerBuilder, models);
            var batchRoute = configuration.Routes["odataBatch"];

            // assert
            foreach ( var route in routes )
            {
                var constraint = route.PathRouteConstraint as VersionedODataPathRouteConstraint;

                if ( constraint == null )
                {
                    continue;
                }

                var apiVersion = constraint.EdmModel.GetAnnotationValue<ApiVersionAnnotation>( constraint.EdmModel ).ApiVersion;
                var versionedRouteName = routeName + "-" + apiVersion.ToString();

                var serviceProviders = (ConcurrentDictionary<string, IServiceProvider>)configuration.Properties[RootContainerMappingsKey];
                var serviceProvider = serviceProviders[versionedRouteName];
                var routingConventions = serviceProvider.GetServices<IODataRoutingConvention>().ToArray();

                routingConventions[0].Should().BeOfType<VersionedAttributeRoutingConvention>();
                routingConventions[1].Should().BeOfType<VersionedMetadataRoutingConvention>();
                routingConventions.OfType<MetadataRoutingConvention>().Should().BeEmpty();
                constraint.RouteName.Should().Be( versionedRouteName );
                route.RoutePrefix.Should().Be( routePrefix );
            }

            batchRoute.Handler.Should().Be( batchHandler );
            batchRoute.RouteTemplate.Should().Be( "api/$batch" );
        }
    }
}